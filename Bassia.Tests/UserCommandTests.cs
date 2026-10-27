namespace Bassia.Tests;

using Bassia.Storage;
using Bassia.Core;
using Bassia.Locks;

/// <summary><c>bassia user ...</c>, <c>whoami</c>, the current user and what is attributed to them, on the git storage.</summary>
public class UserCommandTests
{
	private static async Task<MonorepoFixture> WithUsersAsync()
	{
		var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		var (exit, _, error) = await monorepo.BassiaAsync("user", "add", "-id", "jk", "-name", "Jozef Kosik", "-email", "jozef@example.com", "-contact", "slack @jozef");
		Assert.True(exit == 0, error);
		Assert.Equal(0, (await monorepo.BassiaAsync("user", "add", "-id", "ann", "-name", "Ann Lee", "-email", "ann@example.com")).ExitCode);
		return monorepo;
	}

	[Fact]
	public async Task AddUseWhoAmI_RegisterTheUserInTheMetaRepo_AndTheRegistrationIsTheirs()
	{
		await using var monorepo = await WithUsersAsync();

		var users = await File.ReadAllTextAsync(Path.Combine(monorepo.MetaRepo, "users.toml"));
		Assert.Contains("[[user]]\nid = \"jk\"\nname = \"Jozef Kosik\"\nemail = \"jozef@example.com\"\ncontact = \"slack @jozef\"", users);
		Assert.Equal("Ann Lee <ann@example.com>", await TestEnvironment.GitAsync(monorepo.MetaRepo, "log", "-1", "--format=%an <%ae>"));

		Assert.Contains("does not know who you are", (await monorepo.BassiaAsync("whoami")).Output);
		Assert.Equal(0, (await monorepo.BassiaAsync("user", "use", "jk")).ExitCode);
		var (exit, output, _) = await monorepo.BassiaAsync("whoami");
		Assert.Equal(0, exit);
		Assert.Contains("You are jk: Jozef Kosik <jozef@example.com> (from user.id in config.user.toml)", output);
		Assert.DoesNotContain("user", await TestEnvironment.GitAsync(monorepo.MetaRepo, "status", "--porcelain"));

		Assert.Contains("2 user(s)", (await monorepo.BassiaAsync("user", "list")).Output);
		Assert.Equal(0, (await monorepo.BassiaAsync("user", "set", "ann", "-contact", "teams: Ann")).ExitCode);
		Assert.Contains("contact = \"teams: Ann\"", (await monorepo.BassiaAsync("user", "show", "ann")).Output);
		Assert.Equal("Jozef Kosik <jozef@example.com>", await TestEnvironment.GitAsync(monorepo.MetaRepo, "log", "-1", "--format=%an <%ae>"));
	}

	[Fact]
	public async Task AnUnknownUser_CannotChangeAMonorepoWithUsers_ButMayRead()
	{
		await using var monorepo = await WithUsersAsync();

		var (exit, _, error) = await monorepo.BassiaAsync("component", "create", "lib");

		Assert.Equal(1, exit);
		Assert.Contains("does not know who you are in this monorepo", error);
		Assert.False(Directory.Exists(monorepo.SourceRepo("lib")));
		Assert.Equal(0, (await monorepo.BassiaAsync("component", "list")).ExitCode);

		var previous = Environment.GetEnvironmentVariable(UserCommands.Variable);
		Environment.SetEnvironmentVariable(UserCommands.Variable, "nobody");
		try
		{
			var (badExit, _, badError) = await monorepo.BassiaAsync("component", "create", "lib");
			Assert.Equal(1, badExit);
			Assert.Contains("BASSIA_USER names 'nobody', which is not a registered user", badError);
		}
		finally
		{
			Environment.SetEnvironmentVariable(UserCommands.Variable, previous);
		}
	}

	[Fact]
	public async Task ARun_IsAttributedToItsUser_ViaTheAgent()
	{
		await using var monorepo = await WithUsersAsync();
		Assert.Equal(0, (await monorepo.BassiaAsync("user", "use", "jk")).ExitCode);

		var runId = await monorepo.RunWritingAsync("app", "a.txt", "a");

		var tag = RunMetadata.TagName(runId, 0);
		var repo = monorepo.SourceRepo("app");
		Assert.Equal("Jozef Kosik <jozef@example.com>", await TestEnvironment.GitAsync(repo, "log", "-1", "--format=%an <%ae>", tag));
		// The agent is the run command's program: here the test's 'echo ... > file'.
		Assert.Equal("echo via bassia <jozef@example.com>", await TestEnvironment.GitAsync(repo, "log", "-1", "--format=%cn <%ce>", tag));
		Assert.Equal("echo via bassia <jozef@example.com>", await TestEnvironment.GitAsync(repo, "for-each-ref", "--format=%(taggername) %(taggeremail)", $"refs/tags/{tag}"));
		var (showExit, showOutput, _) = await monorepo.BassiaAsync("agent", "show", runId);
		Assert.Equal(0, showExit);
		Assert.Contains("user = \"jk\"", showOutput);
		Assert.Contains("user = \"jk\"", (await monorepo.BassiaAsync("agent", "list")).Output);
	}

	[Fact]
	public async Task LockCheck_NamesTheUserHoldingAPath_AndHowToReachThem()
	{
		await using var monorepo = await WithUsersAsync();
		Assert.Equal(0, (await monorepo.BassiaAsync("user", "use", "ann")).ExitCode);
		var table = Monorepo.Load(monorepo.Root).Locks;
		using (Actor.Use(new Actor(new UserDefinition("jk", "Jozef Kosik", "jozef@example.com"))))
		{
			await table.UpdateAsync(session =>
			{
				var entry = RunLocks.NewEntry("agent-run-brave-otter-aaaaaa", RunMetadata.Timestamp(), Environment.ProcessId, LocalJobScheduler.CurrentProcessStart(), RunMetadata.Timestamp());
				entry.Holds.Add(new LockHold(new LockKey("app", "README.md", LockKind.File), LockHold.Claimed, RunMetadata.Timestamp()));
				session.Save(entry);
			});
		}

		var (exit, output, error) = await monorepo.BassiaAsync("lock", "check", "-path", "app/README.md");
		var (listExit, listOutput, _) = await monorepo.BassiaAsync("lock", "list");

		Assert.True(exit == 0, error);
		Assert.Contains("app/README.md is held by run brave-otter-aaaaaa (app/README.md of jk (Jozef Kosik, jozef@example.com; slack @jozef))", output);
		Assert.Contains("[[path.holder_users]]", output);
		Assert.Equal(0, listExit);
		Assert.Contains("contact = \"jk (Jozef Kosik, jozef@example.com; slack @jozef)\"", listOutput);
	}
}
