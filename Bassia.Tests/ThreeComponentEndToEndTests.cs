using System.Globalization;
using Bassia.Integration;
using Tomlyn.Model;
using Xunit.Abstractions;
using static Bassia.Tests.EndToEnd;

namespace Bassia.Tests;

/// <summary>
/// The end-to-end proof that work done by parallel agentic sessions on several components ends up on the components'
/// <c>main</c> branches, as checked by Bassia itself. A fresh monorepo holds three components, each a clone of
/// <see cref="EndToEnd.ComponentUrl"/> under its own logical name: two libraries, <c>sortlib</c> and <c>greetlib</c>,
/// and <c>apps</c>, which nests both. Two waves of sessions run at the same time - libraries in one component, the
/// programs that use them in another, one session spanning two components - and each wave is integrated (git merges
/// what it can; a deterministic resolver merges the one real conflict) and advanced onto <c>main</c>. Everything is
/// done through the <c>bassia</c> executable, and the outcome is verified with its own commands: <c>log -run</c>
/// says where each session's commits are and whether they landed on <c>main</c>, <c>log -branch main</c> attributes
/// the mainline to the sessions, <c>component show</c>, <c>run list</c> and <c>integration list</c> agree. A last
/// session builds and runs the programs from the advanced <c>main</c> branches.
///
/// A session's agent is a deterministic stand-in: it applies a patch the test computed against the session's
/// baseline - exactly the edit a coding agent would leave in the run folder - after a short pause, so the sessions
/// of a wave are in flight together. Everything downstream (commit, tag, push, record, triage, merge, resolve,
/// advance) is Bassia's real code path.
///
/// Needs network access for the clones (<c>BASSIA_E2E_COMPONENT_URL</c> points at a mirror instead), hence
/// <c>Category=EndToEnd</c>. Building the programs needs a C compiler on <c>PATH</c>; without one that last check is
/// skipped and the proof says so.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ThreeComponentEndToEndTests(ITestOutputHelper output)
{
	private const string Main = "main";
	private const string SortLib = "sortlib";
	private const string GreetLib = "greetlib";
	private const string Apps = "apps";
	private static readonly string[] Components = [SortLib, GreetLib, Apps];

	/// <summary>One agentic session: what it selects, the prompt it stands for, and the files it writes per component.</summary>
	private sealed record Session(string Name, string Prompt, IReadOnlyDictionary<string, string> Select, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Edit>> Edits)
	{
		public IReadOnlyCollection<string> Touched => Edits.Keys.ToList();

		public string SelectList => string.Join(",", Select.Select(entry => $"{entry.Key}@{entry.Value}"));
	}

	/// <summary>What a session does to one file: its new content, given the content at the session's baseline ("" for a new file).</summary>
	private delegate string Edit(string current);

	private static Edit Write(string content) => _ => content;

	/// <summary>One line added at the end, the way each session documents its work in a README.</summary>
	private static Edit Append(string line) => current => current.Replace("\r", "").TrimEnd('\n') + "\n" + line + "\n";

	private sealed record StartedSession(Session Session, string RunId, string Command);

	private readonly ProofReport proof = new();

	[Fact]
	public async Task ThreeComponents_TwoWavesOfParallelSessions_LandOnMainAsBassiaReports()
	{
		TestEnvironment.EnsureGitIdentity();

		// Not a `using`: with BASSIA_E2E_KEEP the monorepo stays behind so the report's refs can be inspected.
		var temp = new TempDirectory();
		try
		{
			await RunScenarioAsync(temp.Path);
		}
		finally
		{
			var report = proof.ToString();
			output.WriteLine(report);
			output.WriteLine($"Proof written to {WriteProof("bassia-e2e-three-components", report)}");
			if (!KeepMonorepo)
			{
				temp.Dispose();
			}
		}
	}

	private async Task RunScenarioAsync(string temp)
	{
		var root = Path.Combine(temp, "monorepo");
		var sessions = Path.Combine(temp, "sessions");
		Directory.CreateDirectory(root);
		Directory.CreateDirectory(sessions);

		proof.Heading("Bassia three-component agentic sessions - end-to-end proof");
		proof.Fact("Date (UTC)", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
		proof.Fact("Monorepo root", root);
		proof.Fact("Component source", ComponentUrl);
		proof.Fact("bassia", BassiaExecutable);

		// ----- the monorepo: three components from the same upstream, apps nesting both libraries -----

		await OkAsync(root, "init");
		await OkAsync(root, "component", "add", "-url", ComponentUrl, "-name", SortLib);
		await OkAsync(root, "component", "add", "-url", ComponentUrl, "-name", GreetLib);
		var added = await OkAsync(root, "component", "add", "-url", ComponentUrl, "-name", Apps, "-references", $"{SortLib},{GreetLib}");
		Assert.Equal([SortLib, GreetLib], Strings(added, "references"));

		var listed = await OkAsync(root, "component", "list");
		Assert.Equal(Components.Order(), Tables(listed, "component").Select(component => (string)component["name"]).Order());
		var appsShown = await OkAsync(root, "component", "show", Apps);
		Assert.Equal(Main, appsShown["default_branch"]);
		Assert.Equal(Components.Order(), Strings(appsShown, "closure").Order());

		var upstream = new Dictionary<string, string>();
		foreach (var component in Components)
		{
			var tagged = await OkAsync(root, "component", "tag", component, "-tag", "base");
			upstream[component] = (string)tagged["commit"];
		}

		Assert.Single(upstream.Values.Distinct());
		proof.Section("Components");
		proof.Block("bassia graph -format tree", (string)(await OkAsync(root, "graph", "-format", "tree"))["graph"]);
		proof.Fact("Baseline", $"`base` in every component, at the upstream head `{upstream[Apps]}`");

		// ----- wave 1: three sessions at once, started detached -----

		var hello = new Session("hello", "write a hello world program in apps", All("base"), new Dictionary<string, IReadOnlyDictionary<string, Edit>>
		{
			[Apps] = new Dictionary<string, Edit>
			{
				["hello/hello.c"] = Write(HelloC),
				["README.md"] = Append("- `hello/`: prints hello, world")
			}
		});
		var sort = new Session("sort", "implement an insertion sort library in sortlib", new Dictionary<string, string> { [SortLib] = "base" },
			new Dictionary<string, IReadOnlyDictionary<string, Edit>>
			{
				[SortLib] = new Dictionary<string, Edit>
				{
					["sort.h"] = Write(SortH(descending: false)),
					["sort.c"] = Write(SortC(descending: false)),
					["README.md"] = Append("- `sort_ints`: sorts integers in ascending order (insertion sort)")
				}
			});
		var greet = new Session("greet", "add a greeting library in greetlib and a program in apps that uses it", All("base"),
			new Dictionary<string, IReadOnlyDictionary<string, Edit>>
			{
				[GreetLib] = new Dictionary<string, Edit>
				{
					["greet.h"] = Write(GreetH),
					["greet.c"] = Write(GreetC),
					["README.md"] = Append("- `greeting`: formats a greeting for a name")
				},
				[Apps] = new Dictionary<string, Edit>
				{
					["greet/greet.c"] = Write(GreetProgramC),
					["README.md"] = Append("- `greet/`: greets its argument using greetlib")
				}
			});

		var wave1 = await StartDetachedAsync(root, sessions, [hello, sort, greet]);
		await VerifyWaveAsync(root, "Wave 1", wave1);

		// hello and greet both extended apps/README.md at the same place: the one conflict of the scenario.
		var integration1 = await IntegrateAsync(root, temp, "Wave 1", wave1, expectSemantic: (Apps, ["README.md"]));
		await VerifyLandingAsync(root, wave1, integration1);

		var appsReadme = await TestEnvironment.GitAsync(Path.Combine(root, Apps), "show", $"{Main}:README.md");
		Assert.Contains("- `hello/`: prints hello, world", appsReadme);
		Assert.Contains("- `greet/`: greets its argument using greetlib", appsReadme);
		Assert.DoesNotContain("<<<<<<<", appsReadme);
		proof.Block("apps/README.md on main after the resolver's union merge", appsReadme);

		// ----- wave 2: from the advanced main, a program in apps calling sortlib, and a sortlib extension -----

		foreach (var component in Components)
		{
			await OkAsync(root, "component", "tag", component, "-tag", "wave1", "-ref", Main);
		}

		var sortcli = new Session("sortcli", "add a terminal program in apps that sorts its arguments with sortlib", All("wave1"),
			new Dictionary<string, IReadOnlyDictionary<string, Edit>>
			{
				[Apps] = new Dictionary<string, Edit>
				{
					["sortcli/sortcli.c"] = Write(SortCliC),
					["README.md"] = Append("- `sortcli/`: prints its integer arguments sorted, using sortlib")
				}
			});
		var descending = new Session("descending", "add a descending sort to sortlib, with a test program", new Dictionary<string, string> { [SortLib] = "wave1" },
			new Dictionary<string, IReadOnlyDictionary<string, Edit>>
			{
				[SortLib] = new Dictionary<string, Edit>
				{
					["sort.h"] = Write(SortH(descending: true)),
					["sort.c"] = Write(SortC(descending: true)),
					["test_sort.c"] = Write(TestSortC),
					["README.md"] = Append("- `sort_ints_desc`: sorts integers in descending order")
				}
			});

		var wave2 = await StartForegroundAsync(root, sessions, [sortcli, descending]);
		await VerifyWaveAsync(root, "Wave 2", wave2);
		var integration2 = await IntegrateAsync(root, temp, "Wave 2", wave2, expectSemantic: null);
		await VerifyLandingAsync(root, wave2, integration2);

		// ----- the whole picture, from Bassia's point of view -----

		var everyone = wave1.Concat(wave2).ToList();
		await VerifyMainlineAsync(root, everyone);
		await VerifyRecordsAsync(root, everyone, [integration1, integration2]);
		await BuildAndRunFromMainAsync(root);

		proof.Section("Reproduction");
		proof.Line(KeepMonorepo
			? "`BASSIA_E2E_KEEP` is set, so the monorepo above is left in place and every ref named in this report can still be inspected there."
			: "The monorepo is deleted when the test ends; set `BASSIA_E2E_KEEP=1` to leave it behind and inspect the refs named above.");
	}

	private static IReadOnlyDictionary<string, string> All(string tag) => Components.ToDictionary(component => component, _ => tag);

	// ----- sessions -----

	/// <summary>Starts every session with <c>run start -detach</c> at once, then waits for each with <c>run wait</c>.</summary>
	private async Task<IReadOnlyList<StartedSession>> StartDetachedAsync(string root, string sessionsDir, IReadOnlyList<Session> sessions)
	{
		var commands = new List<string>();
		foreach (var session in sessions)
		{
			commands.Add(await AgentCommandAsync(root, sessionsDir, session));
		}

		var results = await Task.WhenAll(sessions.Select((session, index) =>
			BassiaAsync(root, "run", "start", "-select", session.SelectList, "-detach", "-run", commands[index])));

		var started = new List<StartedSession>();
		for (var index = 0; index < sessions.Count; index++)
		{
			var result = AssertOk(results[index], $"run start -detach ({sessions[index].Name})");
			Assert.True((bool)result["detached"]);
			started.Add(new StartedSession(sessions[index], (string)result["run_id"], commands[index]));
		}

		foreach (var session in started)
		{
			var waited = await OkAsync(root, "run", "wait", RunMetadata.Key(session.RunId), "-timeout", "600");
			Assert.Equal("completed", waited["status"]);
		}

		return started;
	}

	/// <summary>Starts every session with a foreground <c>run start</c>, each its own bassia process, all at once.</summary>
	private async Task<IReadOnlyList<StartedSession>> StartForegroundAsync(string root, string sessionsDir, IReadOnlyList<Session> sessions)
	{
		var commands = new List<string>();
		foreach (var session in sessions)
		{
			commands.Add(await AgentCommandAsync(root, sessionsDir, session));
		}

		var results = await Task.WhenAll(sessions.Select((session, index) =>
			BassiaAsync(root, "run", "start", "-select", session.SelectList, "-run", commands[index])));

		return sessions.Select((session, index) =>
		{
			var result = AssertOk(results[index], $"run start ({session.Name})");
			Assert.Equal("completed", result["status"]);
			return new StartedSession(session, (string)result["run_id"], commands[index]);
		}).ToList();
	}

	/// <summary>
	/// The agent stand-in: a patch per touched component, computed against the session's baseline in a scratch clone,
	/// applied in the run folder with <c>git apply</c> after a pause that keeps the wave's sessions in flight together.
	/// The quoted prompt is what Bassia summarizes the run by. <c>git -C</c> reads the patch path relative to the
	/// component's checkout (<c>&lt;root&gt;/.workspace/&lt;run&gt;/&lt;component&gt;</c>), so it is a relative path
	/// that needs no quoting on any platform.
	/// </summary>
	private static async Task<string> AgentCommandAsync(string monorepoRoot, string sessionsDir, Session session)
	{
		var steps = new List<string> { $"echo \"{session.Prompt}\"", TestEnvironment.SleepCommand(2) };
		foreach (var (component, files) in session.Edits)
		{
			var scratch = Path.Combine(sessionsDir, session.Name, component);
			await TestEnvironment.GitAsync(sessionsDir, "clone", "--quiet", Path.Combine(monorepoRoot, component), scratch);
			await TestEnvironment.GitAsync(scratch, "checkout", "--quiet", "--detach", session.Select[component]);
			foreach (var (path, content) in files)
			{
				var file = Path.Combine(scratch, path.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				await File.WriteAllTextAsync(file, content(File.Exists(file) ? await File.ReadAllTextAsync(file) : ""));
			}

			await TestEnvironment.GitAsync(scratch, "add", "--all");
			var patch = Path.Combine(sessionsDir, session.Name, $"{component}.patch");
			await TestEnvironment.GitAsync(scratch, "diff", "--cached", "--binary", $"--output={patch}");
			steps.Add($"git -C {component} apply ../../../../sessions/{session.Name}/{component}.patch");
		}

		return string.Join(" && ", steps);
	}

	/// <summary>
	/// Each session of a wave completed, pushed a result into exactly the components it edited (unchanged elsewhere),
	/// and changed exactly the files it was meant to - as <c>run show</c> and <c>run diff</c> report; the sessions'
	/// recorded intervals all overlap.
	/// </summary>
	private async Task VerifyWaveAsync(string root, string wave, IReadOnlyList<StartedSession> started)
	{
		proof.Section($"{wave}: sessions");
		var intervals = new List<(DateTimeOffset Created, DateTimeOffset Finished)>();
		foreach (var (session, runId, command) in started)
		{
			var shown = await OkAsync(root, "run", "show", RunMetadata.Key(runId));
			Assert.Equal("completed", shown["status"]);
			Assert.False((bool)shown["live"]);
			Assert.Equal(session.SelectList, shown["select"]);
			Assert.Equal(session.Prompt, shown["summary"]);
			foreach (var component in Tables(shown, "component"))
			{
				var name = (string)component["name"];
				Assert.Equal(session.Touched.Contains(name) ? "pushed" : "unchanged", component["result_status"]);
				Assert.Equal(session.Select[name], component["commitish"]);
			}

			var diff = await OkAsync(root, "run", "diff", RunMetadata.Key(runId));
			foreach (var component in Tables(diff, "component"))
			{
				var name = (string)component["name"];
				var expected = session.Edits.TryGetValue(name, out var files) ? files.Keys.Order(StringComparer.Ordinal).ToList() : [];
				Assert.Equal(expected, Strings(component, "files").Order(StringComparer.Ordinal));
			}

			intervals.Add((Timestamp((string)shown["created"]), Timestamp((string)shown["finished"])));
			proof.Fact($"`{session.Name}`", $"run `{runId}`, select `{session.SelectList}`, edits {string.Join("; ", session.Edits.Select(edit => $"{edit.Key}: {string.Join(", ", edit.Value.Keys)}"))}");
			proof.Fact("Command", $"`{command}`");
			proof.Block($"bassia run show {RunMetadata.Key(runId)} (card)", (string)shown["card"]);
		}

		// Some moment lies inside every session's recorded interval: the wave ran in parallel, not one by one.
		var latestStart = intervals.Max(interval => interval.Created);
		var earliestEnd = intervals.Min(interval => interval.Finished);
		Assert.True(latestStart < earliestEnd,
			$"the {wave} sessions did not all overlap: {string.Join(", ", intervals.Select(interval => $"{interval.Created:O}..{interval.Finished:O}"))}");
		proof.Fact("Parallelism", $"all {started.Count} sessions were in flight together for {(earliestEnd - latestStart).TotalSeconds:F3}s");
	}

	// ----- integration -----

	private sealed record Integrated(string Id, IReadOnlyDictionary<string, string> ResultCommits);

	/// <summary>
	/// Plans and integrates a wave with a deterministic resolver, checks how each step was merged, and advances every
	/// <c>main</c>. <paramref name="expectSemantic"/> names the one component and conflicted files the resolver must
	/// merge; everything else must be merged by git.
	/// </summary>
	private async Task<Integrated> IntegrateAsync(string root, string temp, string wave, IReadOnlyList<StartedSession> started,
		(string Component, string[] Files)? expectSemantic)
	{
		var runs = string.Join(",", started.Select(session => RunMetadata.Key(session.RunId)));
		var touched = started.SelectMany(session => session.Session.Touched).Distinct().ToHashSet();

		var plan = await OkAsync(root, "integration", "plan", "-runs", runs);
		Assert.Equal(Components.Where(touched.Contains).Order(), Tables(plan, "component").Select(component => (string)component["name"]).Order());

		var integrated = await OkAsync(root, "integration", "start", "-runs", runs, "-resolve", UnionResolver());
		Assert.Equal("completed", integrated["status"]);
		var id = (string)integrated["integration_id"];

		var resultCommits = new Dictionary<string, string>();
		foreach (var component in Tables(integrated, "component"))
		{
			var name = (string)component["name"];
			Assert.Equal("pushed", component["result_status"]);
			Assert.Equal(Main, component["base_ref"]);
			resultCommits[name] = (string)component["result_commit"];

			var steps = Tables(component, "step");
			var expected = started.Where(session => session.Session.Touched.Contains(name)).Select(session => session.RunId).Order().ToList();
			Assert.Equal(expected, steps.Select(step => (string)step["run_id"]).Order());
			foreach (var step in steps)
			{
				if (expectSemantic is { } semantic && semantic.Component == name && (string)step["triage"] == "conflict")
				{
					Assert.Equal("semantic", step["strategy"]);
					Assert.Equal("resolved", step["outcome"]);
					Assert.Equal(semantic.Files, Strings(step, "conflicts"));
				}
				else
				{
					Assert.Equal("syntactic", step["strategy"]);
					Assert.Equal("merged", step["outcome"]);
				}
			}

			if (expectSemantic is { } wanted && wanted.Component == name)
			{
				Assert.Single(steps, step => (string)step["outcome"] == "resolved");
			}
		}

		Assert.Equal(touched.Order(), resultCommits.Keys.Order());
		proof.Section($"{wave}: integration `{id}`");
		proof.Block($"bassia integration plan -runs {runs} (steps)", (string)plan["steps"]);
		proof.Block("bassia integration start (steps)", (string)integrated["steps"]);

		// Nothing is on main yet: the results are only tagged.
		var before = await OkAsync(root, "log", "-run", runs);
		Assert.Equal(0L, before["landed"]);
		Assert.All(Tables(before, "commit"), commit => Assert.False((bool)commit["on_default_branch"]));
		proof.Fact("Before advance", (string)before["message"]);

		var advanced = await OkAsync(root, "integration", "advance", IntegrationRecord.Key(id));
		proof.Fact("Advance", (string)advanced["message"]);
		return new Integrated(id, resultCommits);
	}

	/// <summary>
	/// The resolver: a union merge of the conflicted file's three stages (base, ours, theirs), which keeps both sides'
	/// additions - deterministic, and all the README conflict of this scenario needs. The stages are staged in
	/// <c>.git</c>, outside the tree that gets committed.
	/// </summary>
	private static string UnionResolver()
	{
		string Stage(string name) => Path.Combine(".git", name);
		return string.Join(" && ",
			$"git show :1:README.md > {Stage("base.md")}",
			$"git show :2:README.md > {Stage("ours.md")}",
			$"git show :3:README.md > {Stage("theirs.md")}",
			$"git merge-file -p --union {Stage("ours.md")} {Stage("base.md")} {Stage("theirs.md")} > README.md");
	}

	// ----- verification through bassia -----

	/// <summary>
	/// <c>bassia log -run</c> per session: its results landed on <c>main</c> through the integration, in exactly the
	/// components it edited, and its commits appear nowhere else; <c>component show</c> has each <c>main</c> at the
	/// integration's result.
	/// </summary>
	private async Task VerifyLandingAsync(string root, IReadOnlyList<StartedSession> started, Integrated integration)
	{
		foreach (var (session, runId, _) in started)
		{
			var log = await OkAsync(root, "log", "-run", RunMetadata.Key(runId));
			var run = Assert.Single(Tables(log, "run"));
			Assert.Equal(runId, run["run_id"]);
			foreach (var component in Tables(run, "component"))
			{
				var name = (string)component["name"];
				var edited = session.Touched.Contains(name);
				Assert.Equal(edited, (bool)component["landed"]);
				Assert.Equal(edited ? new[] { integration.Id } : [], Strings(component, "merged_by"));
				Assert.Equal(Main, component["default_branch"]);
			}

			// One result commit and one merge per edited component, both on main, and nothing anywhere else.
			var commits = Tables(log, "commit");
			Assert.Equal(session.Touched.Order(), commits.Select(commit => (string)commit["component"]).Distinct().Order());
			foreach (var component in session.Touched)
			{
				var mine = commits.Where(commit => (string)commit["component"] == component).ToList();
				Assert.Equal(["integration", "result"], mine.Select(commit => (string)commit["kind"]).Order());
				Assert.All(mine, commit => Assert.Equal(runId, commit["run_id"]));
				Assert.All(mine, commit => Assert.True((bool)commit["on_default_branch"]));
				Assert.Equal(integration.Id, mine.Single(commit => (string)commit["kind"] == "integration")["integration_id"]);
			}

			Assert.Equal((long)session.Touched.Count, log["landed"]);
			proof.Block($"bassia log -run {RunMetadata.Key(runId)} ({session.Name})", $"{log["message"]}\n{log["graph"]}");
		}

		foreach (var (component, resultCommit) in integration.ResultCommits)
		{
			var shown = await OkAsync(root, "component", "show", component);
			var main = Tables(shown, "branch").Single(branch => (string)branch["name"] == Main);
			Assert.Equal(resultCommit, main["commit"]);
			proof.Fact($"`{component}` main", $"`{resultCommit}` = the integration's result ({main["subject"]})");
		}
	}

	/// <summary>
	/// <c>bassia log -component apps -branch main</c> covers apps and the libraries it nests: every session's work is
	/// on the mainline, attributed to it, and only in components it edited.
	/// </summary>
	private async Task VerifyMainlineAsync(string root, IReadOnlyList<StartedSession> everyone)
	{
		var log = await OkAsync(root, "log", "-component", Apps, "-branch", Main, "-limit", "100");
		Assert.Equal(Components.Order(), Strings(log, "components").Order());
		Assert.False((bool)log["has_more"]);
		var commits = Tables(log, "commit");
		var attributed = commits.Where(commit => commit.ContainsKey("run_id")).ToList();

		foreach (var (session, runId, _) in everyone)
		{
			var mine = attributed.Where(commit => (string)commit["run_id"] == runId).ToList();
			Assert.Equal(session.Touched.Order(), mine.Select(commit => (string)commit["component"]).Distinct().Order());
			foreach (var component in session.Touched)
			{
				Assert.Equal(["integration", "result"], mine.Where(commit => (string)commit["component"] == component).Select(commit => (string)commit["kind"]).Order());
			}
		}

		Assert.Equal(everyone.Select(session => session.RunId).Order(), attributed.Select(commit => (string)commit["run_id"]).Distinct().Order());

		// The upstream's own history is there too, untouched, under each component.
		Assert.Equal(3, commits.Count(commit => (string)commit["subject"] == "Initial commit"));
		proof.Section("The mainline");
		proof.Block($"bassia log -component {Apps} -branch {Main}", $"{log["message"]}\n{log["graph"]}");

		var all = await OkAsync(root, "log", "-run", "all");
		Assert.Equal((long)everyone.Sum(session => session.Session.Touched.Count), all["landed"]);
		Assert.Equal(all["results"], all["landed"]);
		proof.Fact("bassia log -run all", (string)all["message"]);
	}

	/// <summary><c>run list</c> and <c>integration list</c>: every session completed, every integration completed and advanced.</summary>
	private async Task VerifyRecordsAsync(string root, IReadOnlyList<StartedSession> everyone, IReadOnlyList<Integrated> integrations)
	{
		var runs = await OkAsync(root, "run", "list", "-limit", "50");
		Assert.Equal(everyone.Select(session => session.RunId).Order(), Tables(runs, "run").Select(run => (string)run["run_id"]).Order());
		Assert.All(Tables(runs, "run"), run => Assert.Equal("completed", run["status"]));

		var listed = await OkAsync(root, "integration", "list");
		Assert.Equal(integrations.Select(integration => integration.Id).Order(), Tables(listed, "integration").Select(entry => (string)entry["integration_id"]).Order());
		Assert.All(Tables(listed, "integration"), entry => Assert.Equal("completed", entry["status"]));
		Assert.All(Tables(listed, "integration"), entry => Assert.True((bool)entry["advanced"]));

		var status = await OkAsync(root, "status");
		Assert.True((bool)status["meta_repo_clean"]);
		proof.Section("Records");
		proof.Block("bassia run list", (string)runs["table"]);
		proof.Block("bassia integration list", (string)listed["table"]);
	}

	/// <summary>
	/// A last session over the three <c>main</c> states builds every program and runs it, so what landed is not only
	/// there but works together: apps' programs against the libraries nested into apps' checkout.
	/// </summary>
	private async Task BuildAndRunFromMainAsync(string root)
	{
		proof.Section("Building from main");
		var compiler = FindCompiler();
		if (compiler is null)
		{
			proof.Line("No C compiler (`cc`, `gcc` or `clang`) on `PATH`, or not a POSIX shell: the programs were not built.");
			return;
		}

		foreach (var component in Components)
		{
			await OkAsync(root, "component", "tag", component, "-tag", "verified", "-ref", Main);
		}

		var build = string.Join(" && ",
			$"cd {Apps}",
			$"{compiler} -I . -o hello.out hello/hello.c", "./hello.out",
			$"{compiler} -I . -o greet.out greet/greet.c greetlib/greet.c", "./greet.out Bassia",
			$"{compiler} -I . -o sortcli.out sortcli/sortcli.c sortlib/sort.c", "./sortcli.out 5 3 9 1",
			$"cd {SortLib}",
			$"{compiler} -o test_sort.out test_sort.c sort.c", "./test_sort.out");
		var result = await BassiaAsync(root, "run", "start", "-select", string.Join(",", Components.Select(component => $"{component}@verified")), "-run", build);
		var toml = AssertOk(result, "run start (build from main)");

		// The build outputs are ignored by the example's C .gitignore, so the session changes no component.
		Assert.Equal("completed", toml["status"]);
		Assert.All(Tables(toml, "component"), component => Assert.Equal("unchanged", component["result_status"]));

		var printed = OutputBeforeResult(result.StandardOutput).Replace("\r", "");
		Assert.Contains("hello, world\n", printed);
		Assert.Contains("Hello, Bassia!\n", printed);
		Assert.Contains("1 3 5 9\n", printed);
		Assert.Contains("9 5 3 1\n", printed);
		proof.Fact("Compiler", compiler);
		proof.Fact("Command", $"`{build}`");
		proof.Block("Output", printed);
	}

	private static string? FindCompiler()
	{
		if (OperatingSystem.IsWindows())
		{
			return null;
		}

		var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
		return new[] { "cc", "gcc", "clang" }.FirstOrDefault(name => path.Any(directory => File.Exists(Path.Combine(directory, name))));
	}

	private static DateTimeOffset Timestamp(string value) =>
		DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

	// ----- what the sessions write -----

	private const string HelloC = """
		#include <stdio.h>

		int main(void)
		{
		    printf("hello, world\n");
		    return 0;
		}

		""";

	private static string SortH(bool descending) => $$"""
		#ifndef SORTLIB_SORT_H
		#define SORTLIB_SORT_H

		#include <stddef.h>

		/* Sorts values[0..count) in ascending order (insertion sort). */
		void sort_ints(int *values, size_t count);
		{{(descending ? "\n/* Sorts values[0..count) in descending order. */\nvoid sort_ints_desc(int *values, size_t count);\n" : "")}}
		#endif

		""";

	private static string SortC(bool descending) => $$"""
		#include "sort.h"

		void sort_ints(int *values, size_t count)
		{
		    for (size_t i = 1; i < count; i++) {
		        int key = values[i];
		        size_t j = i;
		        while (j > 0 && values[j - 1] > key) {
		            values[j] = values[j - 1];
		            j--;
		        }
		        values[j] = key;
		    }
		}
		{{(descending ? """

		void sort_ints_desc(int *values, size_t count)
		{
		    sort_ints(values, count);
		    for (size_t i = 0; i < count / 2; i++) {
		        int swap = values[i];
		        values[i] = values[count - 1 - i];
		        values[count - 1 - i] = swap;
		    }
		}

		""" : "")}}
		""";

	private const string TestSortC = """
		#include <stdio.h>
		#include "sort.h"

		int main(void)
		{
		    int values[] = { 5, 3, 9, 1 };
		    size_t count = sizeof values / sizeof values[0];
		    sort_ints_desc(values, count);
		    for (size_t i = 0; i < count; i++) {
		        printf(i == 0 ? "%d" : " %d", values[i]);
		    }
		    printf("\n");
		    return 0;
		}

		""";

	private const string GreetH = """
		#ifndef GREETLIB_GREET_H
		#define GREETLIB_GREET_H

		#include <stddef.h>

		/* Writes "Hello, <name>!" into buffer and returns it. */
		const char *greeting(const char *name, char *buffer, size_t size);

		#endif

		""";

	private const string GreetC = """
		#include <stdio.h>
		#include "greet.h"

		const char *greeting(const char *name, char *buffer, size_t size)
		{
		    snprintf(buffer, size, "Hello, %s!", name);
		    return buffer;
		}

		""";

	private const string GreetProgramC = """
		#include <stdio.h>
		#include "greetlib/greet.h"

		int main(int argc, char **argv)
		{
		    char buffer[128];
		    puts(greeting(argc > 1 ? argv[1] : "world", buffer, sizeof buffer));
		    return 0;
		}

		""";

	private const string SortCliC = """
		#include <stdio.h>
		#include <stdlib.h>
		#include "sortlib/sort.h"

		int main(int argc, char **argv)
		{
		    size_t count = argc > 1 ? (size_t)(argc - 1) : 0;
		    int *values = malloc((count > 0 ? count : 1) * sizeof *values);
		    for (size_t i = 0; i < count; i++) {
		        values[i] = atoi(argv[i + 1]);
		    }
		    sort_ints(values, count);
		    for (size_t i = 0; i < count; i++) {
		        printf(i == 0 ? "%d" : " %d", values[i]);
		    }
		    printf("\n");
		    free(values);
		    return 0;
		}

		""";
}
