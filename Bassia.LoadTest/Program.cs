namespace Bassia.LoadTest;

using System.Globalization;

/// <summary>
/// The load harness: measures how bassia-server behaves at a chosen scale (feat record bassia-1def6a48). It seeds a
/// synthetic monorepo with history and workspaces, lets simulated agents create workspaces, snapshot and land at once
/// over HTTP, and reports latency, throughput and what each operation asked of PostgreSQL and blob storage.
/// </summary>
internal static class Program
{
	private static string Usage => $"""
		Usage:
		  bassia-loadtest run [-scenario {string.Join('|', Scenario.Presets)}] [-<parameter> <n>]... [options]   run one scenario and report
		  bassia-loadtest sweep [-scenario <name>] -<parameter> <a,b,c> [-<parameter> <n>]... [options]   run it at several values of one parameter
		  bassia-loadtest smoke [options]                                                                  a run of seconds that checks the harness itself
		  bassia-loadtest compare <before.toml> <after.toml> [-threshold <percent>]                        what got slower or cheaper (default: by more than 20%)
		Parameters (each overrides the scenario's value):
		{string.Join('\n', Scenario.Parameters.Select(parameter => $"  -{parameter.Name,-10} {parameter.Meaning}"))}
		Options:
		  -postgres <connection string>   the PostgreSQL of the server the harness hosts (default: BASSIA_TEST_POSTGRES); each run gets its own schema
		  -data <folder>                  keep the seeded state there and reuse it in later runs (default: a temporary state, dropped afterwards)
		  -server <url> [-token <t>]      measure a running server instead (time and requests only; it must be empty unless -no-seed)
		  -no-seed                        with -server: do not seed, run the agents on what is there
		  -no-gc                          skip the garbage-collection dry run
		  -out <folder>                   where the report goes as <name>.toml and <name>.md (default: loadtest-reports)
		A hosted server keeps blobs on local disk and reports PostgreSQL statements and blob calls per operation.
		""";

	public static async Task<int> Main(string[] args)
	{
		if (args.Length == 0 || args[0] is "help" or "-help" or "--help" or "-h")
		{
			Console.Out.WriteLine(Usage);
			return 0;
		}

		try
		{
			return args[0] switch
			{
				"run" => await RunAsync(Switches(args[1..]), false),
				"smoke" => await RunAsync(Switches(["-scenario", "smoke", .. args[1..]]), true),
				"sweep" => await SweepAsync(Switches(args[1..])),
				"compare" => Compare(args[1..]),
				_ => throw new ArgumentException($"Unknown command '{args[0]}'.")
			};
		}
		catch (ArgumentException ex) when (ex.TargetSite?.DeclaringType?.Assembly == typeof(Program).Assembly)
		{
			// A bad switch or parameter, found by the harness itself; an ArgumentException from anywhere else is a defect and keeps its stack trace.
			Console.Error.WriteLine($"bassia-loadtest: {ex.Message}\n{Usage}");
			return 2;
		}
		catch (Exception ex) when (ex is Npgsql.NpgsqlException or Bassia.Core.Storage.VersionException or HttpRequestException or InvalidOperationException)
		{
			Console.Error.WriteLine($"bassia-loadtest: {ex.Message}");
			return 1;
		}
	}

	private static void Log(string message) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

	private static Dictionary<string, string> Switches(IReadOnlyList<string> args)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < args.Count; index++)
		{
			if (!args[index].StartsWith('-'))
			{
				throw new ArgumentException($"'{args[index]}' is not a switch.");
			}

			var name = args[index].TrimStart('-');
			values[name] = index + 1 < args.Count && !args[index + 1].StartsWith('-') ? args[++index] : "true";
		}

		var known = Scenario.Parameters.Select(parameter => parameter.Name).Concat(["scenario", "postgres", "data", "server", "token", "no-seed", "no-gc", "out"]);
		if (values.Keys.Except(known, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } unknown)
		{
			throw new ArgumentException($"Unknown switch '-{unknown}'.");
		}

		return values;
	}

	private static double Number(string name, string text) =>
		double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"-{name} '{text}' is not a number.");

	/// <summary>The scenario the switches describe; a parameter given several values (the swept one) is left at the scenario's own.</summary>
	private static Scenario ScenarioOf(Dictionary<string, string> switches)
	{
		var scenario = Scenario.Preset(switches.GetValueOrDefault("scenario", "small"));
		foreach (var parameter in Scenario.Parameters)
		{
			if (switches.TryGetValue(parameter.Name, out var text) && !text.Contains(','))
			{
				scenario = parameter.Set(scenario, Number(parameter.Name, text));
			}
		}

		return scenario.Validated();
	}

	private static RunOptions OptionsOf(Dictionary<string, string> switches) => new()
	{
		Postgres = switches.GetValueOrDefault("postgres") ?? (Environment.GetEnvironmentVariable("BASSIA_TEST_POSTGRES") is { Length: > 0 } value ? value : null),
		ServerUrl = switches.GetValueOrDefault("server"),
		Token = switches.GetValueOrDefault("token"),
		Data = switches.GetValueOrDefault("data"),
		SeedRemote = !switches.ContainsKey("no-seed"),
		GarbageCollection = !switches.ContainsKey("no-gc")
	};

	private static void Write(Dictionary<string, string> switches, string name, string toml, string markdown)
	{
		var folder = Path.GetFullPath(switches.GetValueOrDefault("out", "loadtest-reports"));
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, name + ".toml"), toml);
		File.WriteAllText(Path.Combine(folder, name + ".md"), markdown);
		Console.Out.WriteLine(markdown);
		Log($"report: {Path.Combine(folder, name + ".toml")} and .md");
	}

	private static async Task<int> RunAsync(Dictionary<string, string> switches, bool smoke)
	{
		var scenario = ScenarioOf(switches);
		var report = await new Runner(scenario, OptionsOf(switches), Log).RunAsync();
		Write(switches, scenario.Name, Reports.Toml(report), Reports.Markdown(report));
		if (smoke)
		{
			var problems = SmokeProblems(report, switches.ContainsKey("server"));
			foreach (var problem in problems)
			{
				Console.Error.WriteLine($"bassia-loadtest: smoke: {problem}");
			}

			if (problems.Count > 0)
			{
				return 1;
			}

			Log("smoke: the harness works");
		}

		return report.Errors == 0 ? 0 : 1;
	}

	/// <summary>What a smoke run must show for the harness to count as working. Nothing here depends on how fast the machine is.</summary>
	private static List<string> SmokeProblems(RunReport report, bool remote)
	{
		var problems = new List<string>();
		var scenario = report.Scenario;
		if (report.Errors > 0)
		{
			problems.Add($"{report.Errors} operation(s) failed.");
		}

		// The files land as seq 1, the history follows, and every agent lands once per round (after a replay, if it must).
		var expected = 1 + scenario.History + (long)scenario.Agents * scenario.Rounds;
		if (report.Seeded && report.MainSeq != expected)
		{
			problems.Add($"mainline is at seq {report.MainSeq}, expected {expected}.");
		}

		if (report.OpenWorkspaces != scenario.Open)
		{
			problems.Add($"{report.OpenWorkspaces} workspaces are open, expected the {scenario.Open} seeded.");
		}

		string[] required =
		[
			Operations.WorkspaceCreate, Operations.Snapshot, Operations.Land + " (landed)", Operations.Land + " (needs-replay)", Operations.LeaseAcquire,
			Operations.WorkspaceList, Operations.WorkspaceResolve, Operations.ConflictOverview, Operations.MainAtSeq, Operations.LogPage, Operations.LogHead,
			Operations.ReconcileAfterSnapshot
		];
		string[] hosted = [Operations.ReconcileScan, Operations.ReconcileCatchUp, Operations.GarbageCollection];
		foreach (var name in remote ? required : [.. required, .. hosted])
		{
			if (report.Operation(name) is not { Count: > 0 } operation)
			{
				problems.Add($"'{name}' was not measured.");
			}
			else if (!remote && name != Operations.ReconcileAfterSnapshot && operation.Statements is not > 0)
			{
				problems.Add($"'{name}' reports no PostgreSQL statements.");
			}
		}

		if (!remote && report.Operation(Operations.Snapshot) is { BlobCalls: not > 0 })
		{
			problems.Add("snapshots report no blob calls.");
		}

		return problems;
	}

	private static async Task<int> SweepAsync(Dictionary<string, string> switches)
	{
		var swept = Scenario.Parameters.Where(parameter => switches.TryGetValue(parameter.Name, out var text) && text.Contains(',')).ToList();
		if (swept.Count != 1)
		{
			throw new ArgumentException("A sweep takes exactly one parameter with several values, for example -closed 100,1000,10000.");
		}

		if (switches.ContainsKey("server") || switches.ContainsKey("data"))
		{
			throw new ArgumentException("A sweep seeds a new state for every value: it runs on a hosted server, without -server and -data.");
		}

		var parameter = swept[0];
		var values = switches[parameter.Name].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(text => Number(parameter.Name, text)).ToList();
		if (values.Count < 2)
		{
			throw new ArgumentException($"-{parameter.Name} needs at least two values to sweep.");
		}

		var scenario = ScenarioOf(switches);
		var runs = new List<(double Value, RunReport Run)>();
		foreach (var value in values)
		{
			Log($"{parameter.Name} = {Parameter.Format(value)}");
			runs.Add((value, await new Runner(parameter.Set(scenario, value).Validated(), OptionsOf(switches), Log).RunAsync()));
		}

		var sweep = new SweepReport(parameter, runs);
		Write(switches, $"{scenario.Name}-sweep-{parameter.Name}", Reports.Toml(sweep), Reports.Markdown(sweep));
		return runs.All(run => run.Run.Errors == 0) ? 0 : 1;
	}

	private static int Compare(IReadOnlyList<string> args)
	{
		var files = args.TakeWhile(arg => !arg.StartsWith('-')).ToList();
		if (files.Count != 2)
		{
			throw new ArgumentException("compare takes two reports: <before.toml> <after.toml>.");
		}

		var rest = args.Skip(2).ToList();
		var threshold = rest.Count == 2 && rest[0].TrimStart('-') == "threshold" ? Number("threshold", rest[1])
			: rest.Count == 0 ? 20 : throw new ArgumentException("compare takes only -threshold <percent>.");
		foreach (var file in files.Where(file => !File.Exists(file)))
		{
			throw new ArgumentException($"No report '{file}'.");
		}

		Console.Out.Write(Reports.Markdown(Reports.Compare(files[0], files[1], threshold), threshold));
		return 0;
	}
}
