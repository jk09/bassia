using System.Diagnostics;
using Bassia.Integration;
using Bassia.Locks;
using Bassia.Split;
using Tomlyn;
using Tomlyn.Model;

namespace Bassia.Tests;

/// <summary>
/// Locking files and folders across parallel runs: the filter and its keys, the lock table's lease (expiry, dead
/// processes, a suspended run losing what others took), deadlocks, and - with real runs, one of them a detached
/// background process - holds, conflicts, waiting with catch-up, and what the integration makes of a lock conflict.
/// </summary>
public class LockTests
{
	private static TomlTable Result(string text) => CliSurfaceTests.Result(text);

	private static IEnumerable<TomlTable> Tables(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? ((TomlTableArray)value).Cast<TomlTable>() : [];

	private static RunMetadataStore Store(MonorepoFixture monorepo) => new(new Bassia.Git.GitClient(monorepo.Root), monorepo.RunsRepo);

	/// <summary>The built bassia, as an agent command calls it.</summary>
	private static string Bassia => $"\"{EndToEnd.BassiaExecutable}\"";

	private static string Then(string first, string second) => OperatingSystem.IsWindows() ? $"{first} & {second}" : $"{first}; {second}";

	private static string MakeFolder(string path) =>
		OperatingSystem.IsWindows() ? $"mkdir {path.Replace('/', '\\')} 2>nul & ver > nul" : $"mkdir -p {path}";

	private static string Append(string path, string content) =>
		OperatingSystem.IsWindows() ? $"echo {content}>> {path.Replace('/', '\\')}" : $"echo '{content}' >> {path}";

	private static async Task SetAsync(MonorepoFixture monorepo, string key, string value, bool user = false)
	{
		var (exitCode, _, error) = user
			? await monorepo.BassiaAsync("config", "set", key, "-value", value, "-user")
			: await monorepo.BassiaAsync("config", "set", key, "-value", value);
		Assert.True(exitCode == 0, error);
	}

	private static async Task<TomlTable> OkAsync(MonorepoFixture monorepo, params string[] args)
	{
		var (exitCode, output, error) = await monorepo.BassiaAsync(args);
		Assert.True(exitCode == 0, $"bassia {string.Join(' ', args)}: {error}");
		return Result(output);
	}

	/// <summary>Starts a detached run and returns its id.</summary>
	private static async Task<string> DetachAsync(MonorepoFixture monorepo, string command, params string[] extra)
	{
		TestEnvironment.EnsureGitIdentity();
		var (exitCode, output, error) = await monorepo.RunStartAsync(["-select", "example@v0", "-detach", .. extra, "-run", command]);
		Assert.True(exitCode == 0, error);
		return TestEnvironment.RunIdOf(output);
	}

	/// <summary>Polls <c>lock list</c> until <paramref name="runId"/> holds something, and returns that listing.</summary>
	private static async Task<TomlTable> UntilHeldAsync(MonorepoFixture monorepo, string runId)
	{
		var clock = Stopwatch.StartNew();
		while (true)
		{
			var list = await OkAsync(monorepo, "lock", "list");
			if (Tables(list, "hold").Any(hold => (string)hold["run_id"] == runId && (string)hold["state"] == "live"))
			{
				return list;
			}

			Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"run {runId} never held anything:\n{TomlSerializer.Serialize(list)}");
			await Task.Delay(200);
		}
	}

	private static async Task WaitForAsync(MonorepoFixture monorepo, string runId)
	{
		var (exitCode, _, error) = await monorepo.BassiaAsync("agent", "wait", runId, "-timeout", "120");
		Assert.True(exitCode == 0, error);
	}

	// ----- keys, patterns and filters -----

	[Fact]
	public void Keys_OverlapWhenTheyShareAFile()
	{
		var file = new LockKey("app", "src/api/x.cs", LockKind.File);
		var folder = new LockKey("app", "src/api", LockKind.Folder);

		Assert.True(file.Overlaps(new LockKey("app", "src/api/x.cs", LockKind.File)));
		Assert.False(file.Overlaps(new LockKey("app", "src/api/y.cs", LockKind.File)));
		Assert.True(folder.Overlaps(file));
		Assert.True(file.Overlaps(folder));
		Assert.True(folder.Overlaps(new LockKey("app", "src", LockKind.Folder)));
		Assert.False(folder.Overlaps(new LockKey("app", "src/apiv2", LockKind.Folder)));
		Assert.False(folder.Overlaps(new LockKey("lib", "src/api", LockKind.Folder)));
		Assert.True(new LockKey("app", "", LockKind.Folder).Overlaps(file));
		Assert.Equal("app/src/api/", folder.Display);
		Assert.Equal("app/src/api/x.cs", file.Display);
	}

	[Fact]
	public void PathPatterns_NameAFolderOnlyWhereThePatternEndsAtIt()
	{
		var patterns = new PathPatterns(["src/*", "!src/generated"]);

		Assert.True(patterns.Names("src/api"));
		Assert.False(patterns.Names("src/api/v1"));
		Assert.True(patterns.Matches("src/api/v1"));
		Assert.False(patterns.Names("src/generated"));
		Assert.False(patterns.Names("src"));
	}

	[Fact]
	public async Task Filter_HoldsAFileAsTheNearestFolderAFolderPatternNames_ElseAsItself()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await monorepo.AddComponentAsync("lib");
		await SetAsync(monorepo, "lock.files", "**/*.cs");
		await SetAsync(monorepo, "lock.folders", "src/*");
		await SetAsync(monorepo, "lock.component.lib.folders", "**");

		var app = LockFilter.For(Monorepo.Load(monorepo.Root), "app");
		var lib = LockFilter.For(Monorepo.Load(monorepo.Root), "lib");
		var own = RunLockPatterns.Parse("app:docs/**", "app:tools", ["app", "lib"]);
		var appWithOwn = LockFilter.For(Monorepo.Load(monorepo.Root), "app", own);

		Assert.Equal(new LockKey("app", "src/api", LockKind.Folder), app.KeyFor("app", "src/api/v1/x.cs"));
		Assert.Equal(new LockKey("app", "src/a.cs", LockKind.File), app.KeyFor("app", "src/a.cs"));
		Assert.Null(app.KeyFor("app", "docs/readme.md"));
		Assert.Equal(new LockKey("lib", "a/b", LockKind.Folder), lib.KeyFor("lib", "a/b/c.txt"));
		Assert.Equal(new LockKey("lib", "README.md", LockKind.File), lib.KeyFor("lib", "README.md"));
		Assert.Equal(new LockKey("app", "docs/readme.md", LockKind.File), appWithOwn.KeyFor("app", "docs/readme.md"));
		Assert.Equal(new LockKey("app", "tools", LockKind.Folder), appWithOwn.KeyFor("app", "tools/build.sh"));
		Assert.True(LockFilter.For(Monorepo.Load(monorepo.Root), "lib", own).KeyFor("lib", "docs/readme.md") is { Kind: LockKind.Folder });

		var error = Assert.Throws<MonorepoException>(() => RunLockPatterns.Parse("nosuch:src/**", null, ["app"]));
		Assert.Contains("not a component of the run", error.Message);
	}

	[Fact]
	public async Task Config_LockKeys_AreLayeredPerComponentAndChecked()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("app");
		await SetAsync(monorepo, "lock.conflict", "manual");
		await SetAsync(monorepo, "lock.component.app.conflict", "merge", user: true);

		var global = await OkAsync(monorepo, "config", "get", "lock.conflict");
		var app = await OkAsync(monorepo, "config", "get", "lock.component.app.conflict");
		var list = await OkAsync(monorepo, "config", "list");

		Assert.Equal("manual", global["value"]);
		Assert.Equal("merge", app["value"]);
		Assert.Equal("user", app["layer"]);
		Assert.Equal(LockConflictPolicy.Merge, LockFilter.ConflictPolicy(Monorepo.Load(monorepo.Root).Config, "app"));
		Assert.Contains(Tables(list, "setting"), setting => (string)setting["key"] == "lock.ttl" && (string)setting["value"] == "120");

		var (ttlExit, _, ttlError) = await monorepo.BassiaAsync("config", "set", "lock.ttl", "-value", "soon");
		var (choiceExit, _, choiceError) = await monorepo.BassiaAsync("config", "set", "lock.conflict", "-value", "ignore");
		var (componentExit, _, componentError) = await monorepo.BassiaAsync("config", "set", "lock.component.nosuch.files", "-value", "**");
		Assert.Equal(1, ttlExit);
		Assert.Contains("positive whole number", ttlError);
		Assert.Equal(1, choiceExit);
		Assert.Contains("resolver | manual | merge", choiceError);
		Assert.Equal(1, componentExit);
		Assert.Contains("not a registered component", componentError);
	}

	// ----- the lock table -----

	[Fact]
	public async Task Table_ExpiredAndDeadHoldsBlockNobody()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		var table = new LockTable(Monorepo.Load(monorepo.Root));
		const string suspended = "agent-run-quiet-fern-a1b2c3";
		const string crashed = "agent-run-brave-otter-d4e5f6";
		using var gone = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "true") { Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "", UseShellExecute = false })!;
		await gone.WaitForExitAsync();

		await table.UpdateAsync(session =>
		{
			// A run elsewhere that stopped renewing (suspended): expired, but kept - it may come back.
			var elsewhere = new LockEntry
			{
				RunId = suspended, Host = "another-machine", Pid = 1, ProcessStart = "x", Started = RunMetadata.Timestamp(),
				Heartbeat = DateTimeOffset.UtcNow.AddHours(-1).ToString("O")
			};
			elsewhere.Holds.Add(new LockHold(new LockKey("example", "a.txt", LockKind.File), LockHold.Changed, RunMetadata.Timestamp()));
			session.Save(elsewhere);

			// A run on this machine whose process is gone: it can never renew, so it holds nothing.
			var dead = RunLocks.NewEntry(crashed, RunMetadata.Timestamp(), gone.Id, "2000-01-01T00:00:00Z", RunMetadata.Timestamp());
			dead.Holds.Add(new LockHold(new LockKey("example", "b.txt", LockKind.File), LockHold.Changed, RunMetadata.Timestamp()));
			session.Save(dead);
		});

		Assert.Equal(LockState.Expired, table.StateOf(table.Find(suspended)!));
		var check = await OkAsync(monorepo, "lock", "check", "example/a.txt,example/b.txt");
		Assert.True((bool)check["free"]);
		Assert.Equal([suspended], ((TomlArray)Tables(check, "path").First()["expired_holders"]).Cast<string>());

		// The next decision drops the dead run's entry; the suspended one stays visible as expired.
		await table.UpdateAsync(_ => { });
		Assert.Null(table.Find(crashed));
		var list = await OkAsync(monorepo, "lock", "list");
		Assert.Equal("expired", Assert.Single(Tables(list, "hold"))["state"]);

		// Not live, so a person breaks it without -force.
		var release = await OkAsync(monorepo, "lock", "release", "-run", "quiet-fern", "-all");
		Assert.Equal(["example/a.txt"], ((TomlArray)release["released"]).Cast<string>());
		Assert.Null(table.Find(suspended));
	}

	[Fact]
	public void ProcessStart_ReadAFewMillisecondsApartAcrossASecondBoundary_IsStillTheSameProcess()
	{
		// The stamp a run records for itself, and another process's reading of the same start just past the second.
		var recorded = "2026-10-24T10:12:00Z";
		var reading = new DateTime(2026, 10, 24, 10, 12, 0, DateTimeKind.Utc);

		Assert.True(JobRegistry.SameStart(reading.AddMilliseconds(999), recorded));
		Assert.True(JobRegistry.SameStart(reading.AddMilliseconds(-3), recorded));
		Assert.False(JobRegistry.SameStart(reading.AddSeconds(30), recorded));
		Assert.False(JobRegistry.SameStart(reading, "not a stamp"));

		// This process, as another process sees it, is alive.
		Assert.True(JobRegistry.IsProcessAlive(Environment.ProcessId, JobRegistry.CurrentProcessStart()));
		Assert.False(JobRegistry.IsProcessAlive(Environment.ProcessId, "2000-01-01T00:00:00Z"));
	}

	[Fact]
	public async Task Table_AWaitThatClosesACycle_IsADeadlock()
	{
		using var temp = new TempDirectory();
		var table = new LockTable(Path.Combine(temp.Path, LockTable.FolderName), TimeSpan.FromMinutes(2));
		const string r = "agent-run-red-fox-aaaaaa", s = "agent-run-sly-cat-bbbbbb";
		var a = new LockKey("app", "a.cs", LockKind.File);
		var b = new LockKey("app", "b.cs", LockKind.File);
		var pid = Environment.ProcessId;
		var start = JobRegistry.CurrentProcessStart();

		await table.UpdateAsync(session =>
		{
			var mine = RunLocks.NewEntry(r, RunMetadata.Timestamp(), pid, start, RunMetadata.Timestamp());
			mine.Holds.Add(new LockHold(a, LockHold.Claimed, RunMetadata.Timestamp()));
			var theirs = RunLocks.NewEntry(s, RunMetadata.Timestamp(), pid, start, RunMetadata.Timestamp());
			theirs.Holds.Add(new LockHold(b, LockHold.Claimed, RunMetadata.Timestamp()));
			theirs.Waits.Add(new LockWait(a, RunMetadata.Timestamp(), pid, start));
			session.Save(mine);
			session.Save(theirs);
		});

		var cycle = await table.UpdateAsync(session => session.DeadlockPath(r, session.Blockers(r, b).Select(pair => pair.Entry.RunId)));
		var none = await table.UpdateAsync(session => session.DeadlockPath(s, session.Blockers(s, a).Select(pair => pair.Entry.RunId)));

		Assert.Equal([r, s, r], cycle);
		Assert.Null(none);
	}

	[Fact]
	public async Task Watcher_ASuspendedRunThatComesBack_LosesWhatAnotherRunTookMeanwhile()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await SetAsync(monorepo, "lock.files", "**");
		var checkout = Path.Combine(monorepo.Workspace, "manual-checkout");
		await TestEnvironment.GitAsync(monorepo.Root, "clone", "--quiet", monorepo.SourceRepo("example"), checkout);
		await File.WriteAllTextAsync(Path.Combine(checkout, "README.md"), "changed\n");

		const string runId = "agent-run-sleepy-owl-c0ffee";
		const string other = "agent-run-eager-bee-123abc";
		var metadata = new RunMetadata { RunId = runId, Status = "started", Created = RunMetadata.Timestamp(), Select = "example", Command = "agent", WorkspacePath = monorepo.Workspace };
		metadata.Components.Add(new ComponentRun { Name = "example", CommitIsh = "main", Commit = "0", Path = checkout, Branch = "agent-run/sleepy-owl-c0ffee" });
		var monorepoModel = Monorepo.Load(monorepo.Root);
		var table = new LockTable(monorepoModel);
		var lines = new List<string>();
		var interval = RunLocks.Interval;
		RunLocks.Interval = TimeSpan.FromHours(1);
		try
		{
			await using var locks = RunLocks.Start(monorepoModel, metadata, line => { lock (lines) { lines.Add(line); } });
			await locks.PollAsync();
			Assert.Equal("example/README.md", Assert.Single(table.Find(runId)!.Holds).Key.Display);

			// The run's process sleeps past the TTL; meanwhile another run takes the file.
			await table.UpdateAsync(session =>
			{
				var mine = session.Find(runId)!;
				mine.Heartbeat = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
				session.Save(mine);
				var taker = RunLocks.NewEntry(other, RunMetadata.Timestamp(), Environment.ProcessId, JobRegistry.CurrentProcessStart(), RunMetadata.Timestamp());
				taker.Holds.Add(new LockHold(new LockKey("example", "README.md", LockKind.File), LockHold.Changed, RunMetadata.Timestamp()));
				session.Save(taker);
			});

			await locks.PollAsync();
			var entry = table.Find(runId)!;
			Assert.Empty(entry.Holds);
			var conflict = Assert.Single(entry.Conflicts);
			Assert.Equal((LockConflict.Held, other, "README.md"), (conflict.Kind, conflict.With, conflict.Path));
			Assert.Contains(lines, line => line.Contains("lost example/README.md while suspended"));

			await locks.StopAsync();
			await locks.FinishAsync();
			Assert.Single(metadata.Components[0].LockConflicts);
			Assert.Null(table.Find(runId));
		}
		finally
		{
			RunLocks.Interval = interval;
		}
	}

	// ----- runs -----

	[Fact]
	public async Task Run_HoldsWhatItChangesWhileLive_ReleasesItWithItsResult_AndForgetsItOnceLanded()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await SetAsync(monorepo, "lock.files", "**");

		var runId = await DetachAsync(monorepo, TestEnvironment.WriteFileCommand("example/a.txt", "a") + " && " + TestEnvironment.SleepCommand(6));
		var list = await UntilHeldAsync(monorepo, runId);
		var hold = Assert.Single(Tables(list, "hold"));
		Assert.Equal(("example/a.txt", "file", "changed"), ((string)hold["display"], (string)hold["kind"], (string)hold["how"]));

		var check = await OkAsync(monorepo, "lock", "check", "-path", "a.txt", "-component", "example");
		Assert.False((bool)check["free"]);
		Assert.Equal("held", Tables(check, "path").Single()["state"]);
		Assert.Contains(RunMetadata.Key(runId), (string)check["message"]);
		Assert.Contains("bassia lock wait", await File.ReadAllTextAsync(Path.Combine(monorepo.RunDir(runId), "AGENTS.md")));
		Assert.True(File.Exists(Path.Combine(monorepo.RunDir(runId), "CLAUDE.md")));
		Assert.Equal(1L, ((TomlTable)(await OkAsync(monorepo, "status"))["locks"])["live"]);
		Assert.Contains("holds example/a.txt", (string)(await OkAsync(monorepo, "agent", "show", runId))["card"]);

		await WaitForAsync(monorepo, runId);
		var released = Assert.Single(Tables(await OkAsync(monorepo, "lock", "list"), "hold"));
		Assert.Equal("released", released["state"]);
		Assert.Equal(RunMetadata.TagName(runId, 0), released["result_tag"]);
		var record = (await Store(monorepo).LoadLatestAsync(runId))!;
		Assert.Equal("example/a.txt", Assert.Single(record.Components[0].Held).Display);
		Assert.Empty(record.Components[0].LockConflicts);

		// Landed on main: nobody needs to catch up with it any more.
		var integration = await OkAsync(monorepo, "integration", "start", "-runs", "all");
		await OkAsync(monorepo, "integration", "advance", (string)integration["integration_id"]);
		Assert.StartsWith("Nothing is held", (string)(await OkAsync(monorepo, "lock", "list"))["message"]);
	}

	[Fact]
	public async Task Run_ChangingAFolderAnotherRunHolds_HasALockConflict_WhichLockConflictDecides()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await SetAsync(monorepo, "lock.folders", "src");

		var holder = await DetachAsync(monorepo,
			MakeFolder("example/src") + " && " + TestEnvironment.WriteFileCommand("example/src/x.txt", "x") + " && " + TestEnvironment.SleepCommand(10));
		await UntilHeldAsync(monorepo, holder);

		// Another file, same locked folder, while the holder is live.
		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-run",
			MakeFolder("example/src") + " && " + TestEnvironment.WriteFileCommand("example/src/y.txt", "y"));
		Assert.True(exitCode == 0, error);
		var result = Result(output[output.IndexOf(TomlResult.Marker, StringComparison.Ordinal)..]);
		var other = (string)result["run_id"];
		Assert.Equal(1L, result["lock_conflicts"]);
		Assert.Contains("lock conflict", (string)result["message"]);
		var conflict = Tables(Tables(result, "component").Single(), "lock_conflict").Single();
		Assert.Equal(("src/y.txt", "held", holder, "example/src/"), ((string)conflict["path"], (string)conflict["kind"], (string)conflict["with"], (string)conflict["with_path"]));
		await WaitForAsync(monorepo, holder);

		async Task<IntegrationStep> StepOfOther()
		{
			var runs = await Store(monorepo).ListLatestAsync();
			var plan = await IntegrationPlanner.PlanAsync(Monorepo.Load(monorepo.Root), runs);
			return plan.Single().Steps.Single(step => step.RunId == other);
		}

		// git merges the two files cleanly; the lock policy decides whether that is trusted.
		var reviewed = await StepOfOther();
		Assert.Equal(MergeStrategy.Semantic, reviewed.Strategy);
		Assert.Contains("lock.conflict = resolver", reviewed.Note);
		Assert.Single(reviewed.LockConflicts);

		await SetAsync(monorepo, "lock.conflict", "manual", user: true);
		var manual = await StepOfOther();
		Assert.Equal(MergeStrategy.Manual, manual.Strategy);
		Assert.Contains("lock.conflict = manual", manual.Note);

		await SetAsync(monorepo, "lock.component.example.conflict", "merge", user: true);
		Assert.Equal(MergeStrategy.Syntactic, (await StepOfOther()).Strategy);

		var plan = await OkAsync(monorepo, "integration", "plan", "-runs", "all");
		Assert.Contains("lock conflict(s): 1", (string)plan["steps"]);

		// An abandoned run's results never land: nobody has to catch up with them.
		await OkAsync(monorepo, "agent", "abandon", holder);
		Assert.DoesNotContain(Tables(await OkAsync(monorepo, "lock", "list"), "hold"), hold => (string)hold["run_id"] == holder);
	}

	[Fact]
	public async Task LockWait_WaitsForTheHolder_ThenBringsItsResultIn_AndTheIntegrationMergesItFirst()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await SetAsync(monorepo, "lock.folders", "src");

		var holder = await DetachAsync(monorepo,
			MakeFolder("example/src") + " && " + TestEnvironment.WriteFileCommand("example/src/x.txt", "from-a") + " && " + TestEnvironment.SleepCommand(8));
		await UntilHeldAsync(monorepo, holder);

		// The second agent asks before it writes, and goes on from the holder's version.
		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-run",
			$"{Bassia} lock wait -path example/src/x.txt > wait.toml && " + Append("example/src/x.txt", "from-b"));
		Assert.True(exitCode == 0, error + output);
		var waiter = TestEnvironment.RunIdOf(output);
		var wait = Result(await File.ReadAllTextAsync(Path.Combine(monorepo.RunDir(waiter), "wait.toml")));
		Assert.True((bool)wait["waited"]);
		Assert.Equal([holder], ((TomlArray)wait["waited_for"]).Cast<string>());
		Assert.Equal(["example/src/x.txt"], ((TomlArray)wait["changed"]).Cast<string>());
		Assert.Contains("re-read", (string)wait["message"]);

		var record = (await Store(monorepo).LoadLatestAsync(waiter))!;
		var component = record.Components.Single();
		Assert.Equal(holder, Assert.Single(component.CaughtUp).Run);
		Assert.Empty(component.LockConflicts);
		var content = await TestEnvironment.GitAsync(monorepo.SourceRepo("example"), "show", $"{component.ResultTag}:src/x.txt");
		Assert.Contains("from-a", content);
		Assert.Contains("from-b", content);

		// Its own change is what it added on top of the holder's.
		var diff = Tables(await OkAsync(monorepo, "agent", "diff", waiter), "component").Single();
		Assert.Equal(["src/x.txt"], ((TomlArray)diff["files"]).Cast<string>());
		Assert.Equal(1L, diff["insertions"]);

		// The holder's result comes first; the waiter's then merges as a fast-forward.
		var steps = (await IntegrationPlanner.PlanAsync(Monorepo.Load(monorepo.Root), await Store(monorepo).ListLatestAsync())).Single().Steps;
		Assert.Equal([holder, waiter], steps.Select(step => step.RunId));
		Assert.All(steps, step => Assert.Equal(MergeStrategy.Syntactic, step.Strategy));

		// A result built on one a human must look at can be no less careful.
		var runs = await Store(monorepo).ListLatestAsync();
		runs.Single(run => run.RunId == holder).Components[0].LockConflicts.Add(new LockConflict("example", "src/x.txt", LockConflict.Held, "agent-run-other-run-abcdef", "example/src/", RunMetadata.Timestamp()));
		var strict = (await IntegrationPlanner.PlanAsync(Monorepo.Load(monorepo.Root), runs, new IntegrationChoices { LockPolicy = LockConflictPolicy.Manual })).Single().Steps;
		Assert.All(strict, step => Assert.Equal(MergeStrategy.Manual, step.Strategy));
		Assert.Contains("builds on the result of run", strict.Single(step => step.RunId == waiter).Note);
	}

	[Fact]
	public async Task ClaimAndTimedWait_FailWhileAnotherRunHoldsThePath()
	{
		await using var monorepo = await MonorepoFixture.CreateAsync();
		await monorepo.AddComponentAsync("example");
		await SetAsync(monorepo, "lock.files", "**");

		var holder = await DetachAsync(monorepo, TestEnvironment.WriteFileCommand("example/a.txt", "a") + " && " + TestEnvironment.SleepCommand(10));
		await UntilHeldAsync(monorepo, holder);

		var (exitCode, output, error) = await monorepo.RunStartAsync("-select", "example@v0", "-run",
			Then(Then($"{Bassia} lock claim -path example/a.txt > claim.toml 2>&1", $"{Bassia} lock wait -path example/a.txt -timeout 1 > wait.toml 2>&1"),
				OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0"));
		Assert.True(exitCode == 0, error);
		var runDir = monorepo.RunDir(TestEnvironment.RunIdOf(output));

		var claim = Result(await File.ReadAllTextAsync(Path.Combine(runDir, "claim.toml")));
		Assert.False((bool)claim["ok"]);
		Assert.StartsWith("Nothing was claimed", (string)claim["error"]);
		Assert.Equal([holder], ((TomlArray)claim["held_by"]).Cast<string>());
		var wait = Result(await File.ReadAllTextAsync(Path.Combine(runDir, "wait.toml")));
		Assert.True((bool)wait["timed_out"]);

		// Outside a run, a lock command that needs one says so.
		var (claimExit, _, claimError) = await monorepo.BassiaAsync("lock", "claim", "example/a.txt");
		Assert.Equal(2, claimExit);
		Assert.Contains("-run is required", claimError);
		await WaitForAsync(monorepo, holder);
	}
}
