namespace Bassia.Tests;

using Bassia.LoadTest;

/// <summary>
/// The load harness's own arithmetic: the seeded tree's layout, who changes which file, and how reports are read,
/// swept and compared. Running a scenario is CI's smoke step, not a test (docs/reference.md, "Load harness").
/// </summary>
public sealed class LoadHarnessTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	[Theory]
	[InlineData(1, 2)]
	[InlineData(300, 10)]
	[InlineData(5_000, 50)]
	[InlineData(20_000, 7)]
	public void SeededPaths_AreDistinct_AndNoDirectoryHoldsMoreThanTheFanout(int files, int fanout)
	{
		var scenario = new Scenario { Files = files, Fanout = fanout };
		var paths = Enumerable.Range(0, files).Select(scenario.PathOf).ToList();
		Assert.Equal(files, paths.Distinct(StringComparer.Ordinal).Count());

		var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		foreach (var path in paths)
		{
			var parts = path.Split('/');
			for (var depth = 1; depth < parts.Length; depth++)
			{
				var directory = string.Join('/', parts[..depth]);
				(children.TryGetValue(directory, out var names) ? names : children[directory] = []).Add(parts[depth]);
			}
		}

		Assert.All(children.Values, names => Assert.InRange(names.Count, 1, fanout));
	}

	[Fact]
	public void AgentsChangeSeededFilesOfTheirOwn_TheSameInEveryRun()
	{
		var scenario = new Scenario { Files = 1_000, Fanout = 10, Agents = 7 };
		var seeded = Enumerable.Range(0, scenario.Files).Select(scenario.PathOf).ToHashSet(StringComparer.Ordinal);
		var owner = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var agent = 0; agent < scenario.Agents; agent++)
		{
			for (var round = 0; round < 3; round++)
			{
				for (var change = 0; change < 5; change++)
				{
					var path = scenario.AgentPath(agent, round, 0, change);
					Assert.Contains(path, seeded);
					Assert.Equal(agent, owner.TryGetValue(path, out var other) ? other : owner[path] = agent);
					Assert.Equal(path, (scenario with { }).AgentPath(agent, round, 0, change));
				}
			}
		}

		Assert.NotEqual(scenario.AgentPath(0, 0, 0, 0), (scenario with { Seed = 2 }).AgentPath(0, 0, 0, 0));
	}

	[Fact]
	public void AgentsThatMeet_MeetAtLeastInPairs()
	{
		foreach (var (agents, overlap) in new[] { (4, 0.5), (8, 0.25), (500, 0.25), (3, 1.0) })
		{
			var scenario = new Scenario { Agents = agents, Overlap = overlap };
			var groups = Enumerable.Range(0, agents).Select(scenario.HotFileOf).OfType<int>().GroupBy(file => file).ToList();
			Assert.NotEmpty(groups);
			Assert.All(groups, group => Assert.True(group.Count() >= 2, $"{agents} agents, overlap {overlap}: a shared file with one agent"));
		}

		Assert.All(Enumerable.Range(0, 8), agent => Assert.Null(new Scenario { Agents = 8, Overlap = 0 }.HotFileOf(agent)));
	}

	[Fact]
	public void EveryPreset_IsValid()
	{
		Assert.All(Scenario.Presets, name => Assert.Equal(name, Scenario.Preset(name).Validated().Name));
		Assert.Throws<ArgumentException>(() => Scenario.Preset("huge"));
		Assert.Throws<ArgumentException>(() => new Scenario { Overlap = 2 }.Validated());
	}

	private static OperationResult Operation(string name, double p50, double requests, double? statements, double? blobs) =>
		new(name, 10, 0, 5, p50, p50 * 2, p50 * 3, p50 * 4, requests, statements, blobs);

	private static RunReport Report(Scenario scenario, params OperationResult[] operations) =>
		new(scenario, "hosted", DateTimeOffset.UnixEpoch, "1.0", true, null, 5, new string('a', 64), 1, 2, 1.5, 4, new BackgroundResult(40, 8, 4), operations);

	[Fact]
	public void ASweepOverTheState_MarksWhatGrewAlthoughTheWorkDidNot()
	{
		var closed = Scenario.Parameters.Single(parameter => parameter.Name == "closed");
		var scenario = new Scenario();
		var sweep = new SweepReport(closed,
		[
			(100, Report(closed.Set(scenario, 100), Operation("list", 10, 200, 1, 600), Operation("land", 30, 1, 19, 5), Operation("scan", 5, 0, 40, 0), Operation("slow", 10, 1, 1, 0))),
			(10_000, Report(closed.Set(scenario, 10_000), Operation("list", 400, 20_000, 1, 60_000), Operation("land", 33, 1, 19.5, 5), Operation("scan", 9, 0, 40, 300), Operation("slow", 90, 1, 1, 0)))
		]);

		var growth = sweep.Growth().ToDictionary(item => item.Operation);
		Assert.True(growth["list"].Grows);
		Assert.Equal(100, growth["list"].Requests);
		Assert.False(growth["land"].Grows);
		Assert.True(growth["scan"].Grows); // blob calls from none to some
		Assert.True(growth["slow"].Grows); // the same statements, each far slower
		Assert.Contains("**grows**", Reports.Markdown(sweep));

		// The same numbers over a load parameter say nothing: more agents ask for more work.
		var agents = Scenario.Parameters.Single(parameter => parameter.Name == "agents");
		Assert.DoesNotContain(new SweepReport(agents, sweep.Runs).Growth(), item => item.Grows);
	}

	[Fact]
	public void Reports_AreReadBack_AndCompared()
	{
		var scenario = new Scenario();
		var before = temp.Combine("before.toml");
		var after = temp.Combine("after.toml");
		File.WriteAllText(before, Reports.Toml(Report(scenario, Operation("list", 100, 200, 1, 600), Operation("land", 30, 1, 19, 5), Operation("tiny", 0.4, 1, 1, 0))));
		File.WriteAllText(after, Reports.Toml(Report(scenario, Operation("list", 10, 1, 1, 0), Operation("land", 31, 1, 30, 5), Operation("tiny", 1.2, 1, 1, 0))));

		var read = Reports.Read(before);
		Assert.Equal(200, read[""]["list"]["requests"]);
		Assert.Equal(19, read[""]["land"]["statements"]);

		var differences = Reports.Compare(before, after, 20);
		Assert.Contains(differences, item => item is { Operation: "list", Measure: "requests", Before: 200, After: 1 });
		Assert.Contains(differences, item => item is { Operation: "list", Measure: "blob calls", After: 0 });
		Assert.Contains(differences, item => item is { Operation: "land", Measure: "statements" } && item.Percent > 50);
		Assert.DoesNotContain(differences, item => item is { Operation: "land", Measure: "p50 ms" }); // 3% is within the threshold
		Assert.DoesNotContain(differences, item => item.Operation == "tiny"); // sub-millisecond medians are noise
		Assert.Empty(Reports.Compare(before, before, 20));

		var text = Reports.Markdown(differences, 20);
		Assert.Contains("Faster or cheaper", text);
		Assert.Contains("Slower or more expensive", text);

		File.WriteAllText(temp.Combine("other.toml"), "kind = \"run\"\n");
		Assert.Throws<ArgumentException>(() => Reports.Read(temp.Combine("other.toml")));
	}

	[Fact]
	public void ASweepReport_IsComparedValueByValue()
	{
		var closed = Scenario.Parameters.Single(parameter => parameter.Name == "closed");
		var scenario = new Scenario();
		SweepReport Sweep(double requests) => new(closed,
		[
			(100, Report(closed.Set(scenario, 100), Operation("list", 10, 200, 1, 600))),
			(1_000, Report(closed.Set(scenario, 1_000), Operation("list", 50, requests, 1, 600)))
		]);
		var before = temp.Combine("before.toml");
		var after = temp.Combine("after.toml");
		File.WriteAllText(before, Reports.Toml(Sweep(2_000)));
		File.WriteAllText(after, Reports.Toml(Sweep(200)));

		var difference = Assert.Single(Reports.Compare(before, after, 20));
		Assert.Equal("closed = 1000", difference.Run);
		Assert.Equal(-90, difference.Percent);
	}
}
