using Bassia.Cli;
using Bassia.Prompt;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>
/// <c>bassia prompt</c> and <c>bassia skill</c>: the reply format, the check against the command table, the step loop
/// with a scripted LLM, skills, backends, and the command end to end with a command backend that prints a canned reply.
/// </summary>
public class PromptCommandTests
{
	/// <summary>An LLM that answers from a script, round by round, and keeps what it was asked.</summary>
	private sealed class ScriptedBackend(params string[] replies) : ILlmBackend
	{
		private readonly Queue<string> replies = new(replies);

		public List<LlmRequest> Requests { get; } = [];

		public string Name => "scripted";

		public string Description => "scripted";

		public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellation)
		{
			Requests.Add(request);
			return Task.FromResult(replies.Count > 0 ? replies.Dequeue() : "done = true\nanswer = \"script ended\"");
		}
	}

	/// <summary>Runs nothing: records each command and answers with a result (ok unless told it fails).</summary>
	private sealed class RecordingExecutor(params string[] failing) : ICommandExecutor
	{
		public List<string> Executed { get; } = [];

		public Task<CommandOutcome> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellation)
		{
			var line = string.Join(' ', args);
			Executed.Add(line);
			var ok = !failing.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));
			var result = TomlResult.Serialize(new Dictionary<string, object?>
			{
				["ok"] = ok,
				["command"] = args[0],
				[ok ? "message" : "error"] = ok ? $"did {line}" : $"could not {line}",
				["component_names"] = new List<string> { "app", "lib" }
			});
			return Task.FromResult(ok ? new CommandOutcome(0, result, "") : new CommandOutcome(1, "", result));
		}
	}

	/// <summary>Runs a command in this process, the way the tests run bassia.</summary>
	private sealed class InProcessExecutor : ICommandExecutor
	{
		public async Task<CommandOutcome> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellation)
		{
			var originalOut = Console.Out;
			var originalError = Console.Error;
			var output = new StringWriter();
			var error = new StringWriter();
			Console.SetOut(output);
			Console.SetError(error);
			try
			{
				var exitCode = await ProgramCli.RunAsync([.. args]);
				return new CommandOutcome(exitCode, output.ToString(), error.ToString());
			}
			finally
			{
				Console.SetOut(originalOut);
				Console.SetError(originalError);
			}
		}
	}

	private static readonly PromptContext Context = new("/work", null, []);

	private static PromptSession Session(ILlmBackend backend, ICommandExecutor executor, Func<string, string?, Confirmation>? confirm = null, IReadOnlyList<Skill>? skills = null) =>
		new(backend, executor, skills ?? [], Context, confirm);

	private static Skill MakeSkill(string name, string body) => new(name, $"how to {name}", body, $"/skills/{name}/SKILL.md");

	// ----- the reply -----

	[Fact]
	public void Reply_InAFenceAfterProse_IsRead()
	{
		var reply = PromptReply.Parse("Sure, here is the plan:\n```toml\ndone = true\nanswer = \"ok\"\n[[command]]\nargs = [\"init\", \"-path\", 'C:\\mono']\nwhy = \"create it\"\n```\n");

		Assert.True(reply.Done);
		Assert.Equal("ok", reply.Answer);
		var command = Assert.Single(reply.Commands);
		Assert.Equal(["init", "-path", "C:\\mono"], command.Args);
		Assert.Equal("create it", command.Why);
	}

	[Fact]
	public void Reply_WithProseFirstAndNoFence_IsReadFromTheFirstTomlLine()
	{
		var reply = PromptReply.Parse("I will check the status first.\n\ndone = false\n[[command]]\nargs = [\"bassia\", \"status\"]\n");

		Assert.False(reply.Done);
		Assert.Equal(["status"], Assert.Single(reply.Commands).Args);
	}

	[Fact]
	public void Reply_ArgsAsOneLine_AreSplitWithQuotes()
	{
		var reply = PromptReply.Parse("[[command]]\nargs = 'run start -select app -prompt \"add a changelog\"'\n");

		Assert.Equal(["run", "start", "-select", "app", "-prompt", "add a changelog"], Assert.Single(reply.Commands).Args);
		Assert.False(reply.Done);
	}

	[Theory]
	[InlineData("I cannot help with that.")]
	[InlineData("[[command]]\nwhy = \"no args\"")]
	public void Reply_NotTheExpectedToml_Fails(string text)
	{
		Assert.Throws<PromptException>(() => PromptReply.Parse(text));
	}

	// ----- the command table -----

	[Theory]
	[InlineData(true, "status")]
	[InlineData(true, "component", "list")]
	[InlineData(true, "-C", "/mono", "log", "-component", "app")]
	[InlineData(true, "bassia", "graph", "-format", "tree")]
	[InlineData(false, "graph", "-out", "g.svg")]
	[InlineData(false, "init", "-path", "/mono")]
	[InlineData(false, "component", "remove", "lib")]
	[InlineData(false, "config", "set", "-key", "llm.backend", "-value", "command")]
	public void Check_ValidCommand_PassesAndKnowsWhetherItOnlyReads(bool readOnly, params string[] args)
	{
		var check = CommandCatalog.Check(args);

		Assert.True(check.Valid, check.Error);
		Assert.Equal(readOnly, check.ReadOnly);
		Assert.NotEqual("bassia", check.Args[0]);
	}

	[Theory]
	[InlineData("Unknown command", "frobnicate")]
	[InlineData("needs one of the subcommands", "component")]
	[InlineData("Unknown switch '-colour'", "graph", "-colour", "red")]
	[InlineData("-name is required", "component", "show")]
	[InlineData("cannot be run from a prompt", "prompt", "do", "it")]
	[InlineData("cannot be run from a prompt", "web")]
	[InlineData("was replaced", "agent")]
	[InlineData("No command given", "-C", "/mono")]
	public void Check_CommandThatDoesNotFit_IsRejectedWithTheReason(string reason, params string[] args)
	{
		var check = CommandCatalog.Check(args);

		Assert.False(check.Valid);
		Assert.Contains(reason, check.Error);
	}

	[Fact]
	public void ReadOnlyCommands_AreAllInTheCommandTable()
	{
		var names = CommandTable.Commands.Select(command => command.FullName).ToHashSet();

		Assert.All(CommandCatalog.ReadOnlyCommands, name => Assert.Contains(name, names));
	}

	[Fact]
	public void SystemPrompt_HasTheCatalogOfEveryCommandButPromptAndWeb()
	{
		var system = PromptSession.SystemPrompt();

		Assert.All(CommandCatalog.Offered, spec => Assert.Contains($"## {spec.FullName}", system));
		Assert.All(CommandCatalog.Offered, spec => Assert.Contains(spec.UsageLine, system));
		Assert.DoesNotContain("## prompt", system);
		Assert.DoesNotContain("## web", system);
	}

	[Fact]
	public void Ask_IsTheRestOfTheLineFromTheFirstBareWord()
	{
		var spec = CommandTable.Group("prompt").Single();

		var invocation = Invocation.Parse(spec, ["-yes", "-model", "opus", "list", "runs", "-status", "failed", "in C:\\my mono"]);

		Assert.True(invocation.Has("yes"));
		Assert.Equal("opus", invocation.Get("model"));
		Assert.Equal("list runs -status failed \"in C:\\my mono\"", invocation.Get("ask"));
	}

	// ----- the step loop -----

	[Fact]
	public async Task Session_DoneWithCommands_RunsThemAndAnswers()
	{
		var executor = new RecordingExecutor();
		var backend = new ScriptedBackend("done = true\nanswer = \"Created it.\"\n[[command]]\nargs = [\"init\", \"-path\", '/mono']\n");

		var outcome = await Session(backend, executor, confirm: (_, _) => Confirmation.Yes).RunAsync(new PromptOptions("make a monorepo in /mono"), CancellationToken.None);

		Assert.True(outcome.Ok);
		Assert.Equal("Created it.", outcome.Message);
		Assert.Equal(["init -path /mono"], executor.Executed);
		var step = Assert.Single(outcome.Steps);
		Assert.Equal("ok", step.Status);
		Assert.Equal("did init -path /mono", step.Message);
		Assert.Single(backend.Requests);
		Assert.Contains("make a monorepo in /mono", backend.Requests[0].Prompt);
		Assert.Contains("not inside a Bassia monorepo", backend.Requests[0].Prompt);
	}

	[Fact]
	public async Task Session_NotDone_SendsTheResultsBackForTheNextRound()
	{
		var executor = new RecordingExecutor();
		var backend = new ScriptedBackend(
			"done = false\n[[command]]\nargs = [\"status\"]\n",
			"done = true\nanswer = \"There are two components: app and lib.\"\n");

		var outcome = await Session(backend, executor).RunAsync(new PromptOptions("how many components?"), CancellationToken.None);

		Assert.True(outcome.Ok);
		Assert.Equal(2, outcome.Rounds);
		Assert.Equal("There are two components: app and lib.", outcome.Message);
		Assert.Equal(["status"], executor.Executed);
		var second = backend.Requests[1].Prompt;
		Assert.Contains("# Results so far", second);
		Assert.Contains("$ bassia status   # ok, exit code 0", second);
		Assert.Contains(TomlResult.Marker, second);
		Assert.Contains("component_names = [\"app\", \"lib\"]", second);
	}

	[Fact]
	public async Task Session_RejectedCommand_IsNotRunAndTheReasonGoesBack()
	{
		var executor = new RecordingExecutor();
		var backend = new ScriptedBackend(
			"done = true\n[[command]]\nargs = [\"component\", \"show\", \"-colour\", \"red\"]\n[[command]]\nargs = [\"status\"]\n",
			"done = true\nanswer = \"fixed\"\n[[command]]\nargs = [\"component\", \"show\", \"app\"]\n");

		var outcome = await Session(backend, executor).RunAsync(new PromptOptions("show app"), CancellationToken.None);

		Assert.True(outcome.Ok);
		Assert.Equal(["component show app"], executor.Executed);
		Assert.Equal(["rejected", "skipped", "ok"], outcome.Steps.Select(step => step.Status));
		Assert.Contains("Unknown switch '-colour'", backend.Requests[1].Prompt);
	}

	[Fact]
	public async Task Session_FailedCommand_SkipsTheRestOfTheRoundAndEndsNotOkWhenTheLlmGivesUp()
	{
		var executor = new RecordingExecutor("component add");
		var backend = new ScriptedBackend(
			"done = true\n[[command]]\nargs = [\"component\", \"add\", \"-url\", \"x\"]\n[[command]]\nargs = [\"component\", \"tag\", \"x\", \"-tag\", \"v1\"]\n",
			"done = true\nanswer = \"The URL does not exist.\"\n");

		var outcome = await Session(backend, executor).RunAsync(new PromptOptions("add x", Yes: true), CancellationToken.None);

		Assert.False(outcome.Ok);
		Assert.Equal("The URL does not exist.", outcome.Message);
		Assert.Equal(["component add -url x"], executor.Executed);
		Assert.Equal(["failed", "skipped"], outcome.Steps.Select(step => step.Status));
		Assert.Contains("# failed, exit code 1", backend.Requests[1].Prompt);
	}

	[Fact]
	public async Task Session_ChangingCommandWithNoOneToAsk_IsDeclinedAndNothingRuns()
	{
		var executor = new RecordingExecutor();
		var backend = new ScriptedBackend("done = true\n[[command]]\nargs = [\"status\"]\n[[command]]\nargs = [\"component\", \"remove\", \"lib\"]\n");

		var outcome = await Session(backend, executor, confirm: null).RunAsync(new PromptOptions("remove lib"), CancellationToken.None);

		Assert.False(outcome.Ok);
		Assert.Contains("-yes", outcome.Message);
		Assert.Equal(["status"], executor.Executed);
		Assert.Equal("declined", outcome.Steps[^1].Status);
	}

	[Fact]
	public async Task Session_ConfirmationAll_ApprovesTheRestAndNoDeclinesStops()
	{
		var asked = new List<string>();
		var executor = new RecordingExecutor();
		var backend = new ScriptedBackend("done = true\n[[command]]\nargs = [\"component\", \"tag\", \"a\", \"-tag\", \"v1\"]\n[[command]]\nargs = [\"component\", \"tag\", \"b\", \"-tag\", \"v1\"]\n");

		var outcome = await Session(backend, executor, confirm: (line, _) => { asked.Add(line); return Confirmation.All; })
			.RunAsync(new PromptOptions("tag a and b"), CancellationToken.None);

		Assert.True(outcome.Ok);
		Assert.Equal(["bassia component tag a -tag v1"], asked);
		Assert.Equal(2, executor.Executed.Count);

		var declined = await Session(new ScriptedBackend("done = true\n[[command]]\nargs = [\"init\"]\n"), executor, confirm: (_, _) => Confirmation.No)
			.RunAsync(new PromptOptions("init"), CancellationToken.None);
		Assert.False(declined.Ok);
		Assert.Equal("Declined 'bassia init'.", declined.Message);
		Assert.Equal(2, executor.Executed.Count);
	}

	[Fact]
	public async Task Session_DryRun_RunsReadOnlyCommandsAndPlansFromTheFirstChange()
	{
		var executor = new RecordingExecutor();
		var backend = new ScriptedBackend(
			"done = false\n[[command]]\nargs = [\"component\", \"list\"]\n",
			"done = true\n[[command]]\nargs = [\"component\", \"remove\", \"lib\"]\n[[command]]\nargs = [\"status\"]\n");

		var outcome = await Session(backend, executor).RunAsync(new PromptOptions("remove lib", DryRun: true, Yes: true), CancellationToken.None);

		Assert.True(outcome.Ok);
		Assert.StartsWith("Dry run: 2 command(s) planned", outcome.Message);
		Assert.Equal(["component list"], executor.Executed);
		Assert.Equal(["ok", "planned", "planned"], outcome.Steps.Select(step => step.Status));
	}

	[Fact]
	public async Task Session_RoundsRunOut_EndsNotOk()
	{
		var backend = new ScriptedBackend(Enumerable.Repeat("done = false\n[[command]]\nargs = [\"status\"]\n", 5).ToArray());

		var outcome = await Session(backend, new RecordingExecutor()).RunAsync(new PromptOptions("loop", MaxRounds: 3), CancellationToken.None);

		Assert.False(outcome.Ok);
		Assert.Equal(3, outcome.Rounds);
		Assert.Contains("Not finished after 3 round(s)", outcome.Message);
	}

	[Fact]
	public async Task Session_UnreadableReply_FailsWithTheReply()
	{
		var error = await Assert.ThrowsAsync<PromptException>(() =>
			Session(new ScriptedBackend("Sorry, no."), new RecordingExecutor()).RunAsync(new PromptOptions("x"), CancellationToken.None));

		Assert.Contains("The reply was: Sorry, no.", error.Message);
	}

	// ----- skills -----

	[Fact]
	public async Task Session_OffersSkillDescriptionsAndLoadsABodyOnRequest()
	{
		var skills = new[] { MakeSkill("release", "Tag every component release-<n>."), MakeSkill("onboard", "Add and tag v1.") };
		var backend = new ScriptedBackend("load_skills = [\"release\"]\n", "done = true\nanswer = \"ok\"\n");

		var outcome = await Session(backend, new RecordingExecutor(), skills: skills).RunAsync(new PromptOptions("cut a release"), CancellationToken.None);

		Assert.True(outcome.Ok);
		Assert.Equal(["release"], outcome.LoadedSkills);
		Assert.Contains("- release: how to release", backend.Requests[0].Prompt);
		Assert.DoesNotContain("Tag every component", backend.Requests[0].Prompt);
		Assert.Contains("## Skill: release", backend.Requests[1].Prompt);
		Assert.Contains("Tag every component release-<n>.", backend.Requests[1].Prompt);
		Assert.DoesNotContain("Add and tag v1.", backend.Requests[1].Prompt);
	}

	[Fact]
	public async Task Session_SkillSwitchAndSlashMention_PreloadSkills_AndAnUnknownSkillSwitchFails()
	{
		var skills = new[] { MakeSkill("release", "RELEASE BODY"), MakeSkill("onboard", "ONBOARD BODY") };
		var backend = new ScriptedBackend("done = true\nanswer = \"ok\"\n");

		var outcome = await Session(backend, new RecordingExecutor(), skills: skills)
			.RunAsync(new PromptOptions("/onboard the lib at /tmp/release", Skills: ["release"]), CancellationToken.None);

		Assert.Equal(["release", "onboard"], outcome.LoadedSkills);
		Assert.Contains("RELEASE BODY", backend.Requests[0].Prompt);
		Assert.Contains("ONBOARD BODY", backend.Requests[0].Prompt);

		var error = await Assert.ThrowsAsync<PromptException>(() =>
			Session(new ScriptedBackend(), new RecordingExecutor(), skills: skills).RunAsync(new PromptOptions("x", Skills: ["nope"]), CancellationToken.None));
		Assert.Contains("Known skills: release, onboard", error.Message);
	}

	[Fact]
	public void SkillFile_FrontMatterNamesAndDescribesIt_OtherwiseTheFolderAndFirstLineDo()
	{
		var withFrontMatter = SkillStore.Parse("folder", "---\nname: release\ndescription: \"Cut a release.\"\n---\n\n# Steps\n1. tag\n", "p");
		Assert.Equal("release", withFrontMatter.Name);
		Assert.Equal("Cut a release.", withFrontMatter.Description);
		Assert.Equal("# Steps\n1. tag", withFrontMatter.Body);

		var plain = SkillStore.Parse("onboard", "# Onboard a library\n\nAdd it.\n", "p");
		Assert.Equal("onboard", plain.Name);
		Assert.Equal("Onboard a library", plain.Description);
	}

	// ----- backends -----

	[Fact]
	public void Backends_ClaudeTurnsToolsOff_CommandTakesTheModelPlaceholder_UnknownFails()
	{
		var claude = LlmBackends.Create("claude", new LlmSettings("claude -p", "opus", "."));
		Assert.Equal("claude -p --tools \"\" --output-format text --no-session-persistence --model opus", claude.Description);

		var command = LlmBackends.Create("COMMAND", new LlmSettings("ollama run {model}", "llama3", "."));
		Assert.Equal("ollama run llama3", command.Description);
		Assert.Throws<PromptException>(() => LlmBackends.Create("command", new LlmSettings("llm", "gpt", ".")).Description);

		var error = Assert.Throws<PromptException>(() => LlmBackends.Create("gpt", new LlmSettings("x", null, ".")));
		Assert.Contains("Known backends: claude, command", error.Message);
	}

	[Fact]
	public async Task CommandBackend_PassesThePromptOnStdinAndReturnsStdout()
	{
		using var temp = new TempDirectory();
		var backend = LlmBackends.Create("command", new LlmSettings(OperatingSystem.IsWindows() ? "findstr ASK" : "grep ASK", null, temp.Path));

		var reply = await backend.CompleteAsync(new LlmRequest("system", "ASK: hello"), CancellationToken.None);

		Assert.Equal("ASK: hello", reply);
	}

	[Fact]
	public async Task CommandBackend_FailingCommand_FailsWithItsExitCodeAndOutput()
	{
		using var temp = new TempDirectory();
		var backend = LlmBackends.Create("command", new LlmSettings(OperatingSystem.IsWindows() ? "echo quota exceeded 1>&2 & exit /b 4" : "echo quota exceeded >&2; exit 4", null, temp.Path));

		var error = await Assert.ThrowsAsync<PromptException>(() => backend.CompleteAsync(new LlmRequest("s", "p"), CancellationToken.None));

		Assert.Contains("failed with exit code 4", error.Message);
		Assert.Contains("quota exceeded", error.Message);
	}

	// ----- end to end -----

	private static string CannedReplyCommand(string path) => OperatingSystem.IsWindows() ? $"type \"{path}\"" : $"cat '{path}'";

	[Fact]
	public async Task Prompt_EndToEnd_PlansWithTheConfiguredCommandBackendAndRunsTheCommands()
	{
		using var temp = new TempDirectory();
		var originalExecutor = PromptCommands.ExecutorFactory;
		PromptCommands.ExecutorFactory = _ => new InProcessExecutor();
		try
		{
			var (initCode, _, initError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "init");
			Assert.True(initCode == 0, initError);
			var skillDirectory = Path.Combine(SkillStore.DirectoryOf(temp.Path), "settle");
			Directory.CreateDirectory(skillDirectory);
			File.WriteAllText(Path.Combine(skillDirectory, SkillStore.FileName), "---\ndescription: Settle the workspace.\n---\nUse ws.\n");

			var reply = Path.Combine(temp.Path, "reply.toml");
			File.WriteAllText(reply, "Plan:\n```toml\ndone = true\nanswer = \"Moved the workspace to ws.\"\n[[command]]\nargs = [\"config\", \"set\", \"-key\", \"workspace.path\", \"-value\", \"ws\"]\nwhy = \"as asked\"\n```\n");
			var (setCode, _, setError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "config", "set", "llm.backend", "-value", "command");
			Assert.True(setCode == 0, setError);

			var (exitCode, output, error) = await TestEnvironment.RunInDirectoryAsync(temp.Path,
				"prompt", "-llm", CannedReplyCommand(reply), "-yes", "/settle", "put", "the", "workspace", "in", "ws");

			Assert.True(exitCode == 0, error);
			var result = CliSurfaceTests.Result(output);
			Assert.Equal("prompt", result["command"]);
			Assert.Equal("Moved the workspace to ws.", result["message"]);
			Assert.Equal("command", result["backend"]);
			Assert.Equal(new[] { "settle" }, ((TomlArray)result["skills_loaded"]).Cast<string>());
			var step = Assert.Single(((TomlTableArray)result["step"]).Cast<TomlTable>());
			Assert.Equal("ok", step["status"]);
			Assert.Equal("bassia config set -key workspace.path -value ws", step["command_line"]);
			Assert.Equal(Path.Combine(temp.Path, "ws"), Monorepo.Load(temp.Path).WorkspaceDir);
		}
		finally
		{
			PromptCommands.ExecutorFactory = originalExecutor;
		}
	}

	[Fact]
	public async Task Prompt_UnknownBackend_FailsWithTheKnownOnes()
	{
		using var temp = new TempDirectory();

		var (exitCode, _, error) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "prompt", "-backend", "gpt", "hello");

		Assert.Equal(1, exitCode);
		Assert.Contains("Known backends: claude, command", error);
	}

	[Fact]
	public async Task Prompt_WithoutAnAsk_IsAUsageError()
	{
		var (exitCode, _, error) = await TestEnvironment.RunAsync("prompt", "-yes");

		Assert.Equal(2, exitCode);
		Assert.Contains("-ask is required", error);
	}

	[Fact]
	public async Task SkillCommands_ListAndShowTheMetaReposSkills()
	{
		using var temp = new TempDirectory();
		var (initCode, _, initError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "init");
		Assert.True(initCode == 0, initError);

		var (emptyCode, emptyOutput, _) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "list");
		Assert.Equal(0, emptyCode);
		Assert.StartsWith("No skills.", (string)CliSurfaceTests.Result(emptyOutput)["message"]);

		var directory = Path.Combine(SkillStore.DirectoryOf(temp.Path), "release");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, SkillStore.FileName), "---\nname: release\ndescription: Cut a release.\n---\nTag everything.\n");

		var (listCode, listOutput, _) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "list");
		Assert.Equal(0, listCode);
		var entry = Assert.Single(((TomlTableArray)CliSurfaceTests.Result(listOutput)["skill"]).Cast<TomlTable>());
		Assert.Equal("release", entry["name"]);
		Assert.Equal("Cut a release.", entry["description"]);

		var (showCode, showOutput, _) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "show", "release");
		Assert.Equal(0, showCode);
		Assert.Equal("Tag everything.\n", CliSurfaceTests.Result(showOutput)["body"]);

		var (missingCode, _, missingError) = await TestEnvironment.RunInDirectoryAsync(temp.Path, "skill", "show", "nope");
		Assert.Equal(1, missingCode);
		Assert.Contains("Known skills: release", missingError);
	}
}
