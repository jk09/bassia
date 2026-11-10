namespace Bassia.LoadTest;

using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// One operation's numbers: how often it ran, its throughput while it ran, its latency, and what one execution asked
/// of the server on average - HTTP requests, PostgreSQL statements and blob-store calls (the last two only from a
/// server the harness hosts).
/// </summary>
internal sealed record OperationResult(string Name, long Count, long Errors, double Throughput, double P50, double P95, double P99, double Max,
	double Requests, double? Statements, double? BlobCalls);

/// <summary>What the server did outside requests while the agents worked: its background reconciliation.</summary>
internal sealed record BackgroundResult(long Statements, long BlobCalls, long Landings);

/// <summary>The report of one run.</summary>
internal sealed record RunReport(
	Scenario Scenario,
	string Server,
	DateTimeOffset Time,
	string Version,
	bool Seeded,
	SeedResult? Seed,
	long MainSeq,
	string Tree,
	int OpenWorkspaces,
	int ClosedWorkspaces,
	double LoadSeconds,
	long Landings,
	BackgroundResult? Background,
	IReadOnlyList<OperationResult> Operations)
{
	public long Errors => Operations.Sum(operation => operation.Errors);

	public OperationResult? Operation(string name) => Operations.FirstOrDefault(operation => operation.Name == name);
}

/// <summary>How one operation changed between the smallest and the largest value of a sweep.</summary>
internal sealed record Growth(string Operation, double From, double To, double Latency, double? Statements, double? BlobCalls, double? Requests, bool Grows);

/// <summary>The report of a sweep: the same scenario at several values of one parameter.</summary>
internal sealed record SweepReport(Parameter Parameter, IReadOnlyList<(double Value, RunReport Run)> Runs)
{
	/// <summary>Growth of a cost by this factor marks the operation.</summary>
	public const double CountFactor = 1.5;

	/// <summary>Growth of the median latency by this factor marks the operation, when the median is not negligible.</summary>
	public const double LatencyFactor = 3;

	public const double LatencyFloorMilliseconds = 20;

	/// <summary>
	/// Each operation at the largest value against the smallest. An operation is marked as growing when the parameter
	/// sizes the state (so the work asked for did not change) and its statements, blob calls or requests grew by half,
	/// or its median latency tripled.
	/// </summary>
	public IReadOnlyList<Growth> Growth()
	{
		var ordered = Runs.OrderBy(run => run.Value).ToList();
		var (low, high) = (ordered[0], ordered[^1]);
		var result = new List<Growth>();
		foreach (var to in high.Run.Operations)
		{
			if (low.Run.Operation(to.Name) is not { Count: > 0 } from || to.Count == 0)
			{
				continue;
			}

			// From nothing to something is growth too, though it has no factor.
			double? Ratio(double? a, double? b) => a is null || b is null ? null : a > 0 ? b / a : b >= 1 ? double.PositiveInfinity : null;
			var latency = from.P50 > 0 ? to.P50 / from.P50 : 1;
			var statements = Ratio(from.Statements, to.Statements);
			var blobs = Ratio(from.BlobCalls, to.BlobCalls);
			var requests = Ratio(from.Requests, to.Requests);
			var grows = Parameter.State && (statements >= CountFactor || blobs >= CountFactor || requests >= CountFactor
				|| (latency >= LatencyFactor && to.P50 >= LatencyFloorMilliseconds));
			result.Add(new Growth(to.Name, low.Value, high.Value, latency, statements, blobs, requests, grows));
		}

		return result;
	}
}

/// <summary>An operation that differs between two reports.</summary>
internal sealed record Difference(string Run, string Operation, string Measure, double Before, double After)
{
	public double Percent => Before == 0 ? (After == 0 ? 0 : double.PositiveInfinity) : (After - Before) / Before * 100;
}

/// <summary>Writes reports as TOML and Markdown, reads them back, and compares two.</summary>
internal static class Reports
{
	public const string Marker = "# bassia load report";

	/// <summary>The order operations are listed in; others follow by name.</summary>
	private static readonly string[] Order =
	[
		Operations.WorkspaceCreate, Operations.LeaseAcquire, Operations.Snapshot, Operations.ReconcileAfterSnapshot, Operations.Sync,
		Operations.Land + " (landed)", Operations.Land + " (needs-replay)", Operations.WorkspaceClose, Operations.LeaseRelease,
		Operations.ReconcileScan, Operations.ReconcileCatchUp, Operations.ConflictOverview, Operations.WorkspaceList, Operations.WorkspaceResolve,
		Operations.MainAtSeq, Operations.LogPage, Operations.LogHead, Operations.GarbageCollection
	];

	public static IEnumerable<OperationResult> Ordered(IEnumerable<OperationResult> operations) =>
		operations.OrderBy(operation => Array.IndexOf(Order, operation.Name) is var at and >= 0 ? at : int.MaxValue).ThenBy(operation => operation.Name, StringComparer.Ordinal);

	private static string N(double value) => double.IsPositiveInfinity(value) ? "inf" : value.ToString("0.###", CultureInfo.InvariantCulture);

	private static string S(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

	// ----- TOML -----

	public static string Toml(RunReport report)
	{
		var text = new StringBuilder();
		text.AppendLine(Marker).AppendLine("kind = \"run\"");
		Body(text, report, "");
		return text.ToString();
	}

	public static string Toml(SweepReport sweep)
	{
		var text = new StringBuilder();
		text.AppendLine(Marker).AppendLine("kind = \"sweep\"");
		text.AppendLine($"parameter = {S(sweep.Parameter.Name)}");
		text.AppendLine($"values = [{string.Join(", ", sweep.Runs.Select(run => N(run.Value)))}]");
		foreach (var growth in sweep.Growth())
		{
			text.AppendLine().AppendLine("[[growth]]");
			text.AppendLine($"operation = {S(growth.Operation)}");
			text.AppendLine($"from = {N(growth.From)}").AppendLine($"to = {N(growth.To)}");
			text.AppendLine($"latency = {N(growth.Latency)}");
			if (growth.Requests is { } requests)
			{
				text.AppendLine($"requests = {N(requests)}");
			}

			if (growth.Statements is { } statements)
			{
				text.AppendLine($"statements = {N(statements)}");
			}

			if (growth.BlobCalls is { } blobs)
			{
				text.AppendLine($"blob_calls = {N(blobs)}");
			}

			text.AppendLine($"grows = {(growth.Grows ? "true" : "false")}");
		}

		foreach (var (value, run) in sweep.Runs)
		{
			text.AppendLine().AppendLine("[[run]]").AppendLine($"value = {N(value)}");
			Body(text, run, "run.");
		}

		return text.ToString();
	}

	private static void Body(StringBuilder text, RunReport report, string prefix)
	{
		var scenario = report.Scenario;
		text.AppendLine($"time = {S(report.Time.ToString("O", CultureInfo.InvariantCulture))}");
		text.AppendLine($"version = {S(report.Version)}");
		text.AppendLine($"server = {S(report.Server)}");
		text.AppendLine($"errors = {report.Errors}");
		text.AppendLine().AppendLine($"[{prefix}scenario]").AppendLine($"name = {S(scenario.Name)}");
		foreach (var parameter in Scenario.Parameters)
		{
			text.AppendLine($"{parameter.Name} = {N(parameter.Get(scenario))}");
		}

		text.AppendLine().AppendLine($"[{prefix}seed]").AppendLine($"seeded = {(report.Seeded ? "true" : "false")}");
		if (report.Seed is { } seed)
		{
			text.AppendLine($"seconds = {N(seed.Seconds)}");
			text.AppendLine($"files_seconds = {N(seed.FilesSeconds)}");
			text.AppendLine($"history_seconds = {N(seed.HistorySeconds)}");
			text.AppendLine($"workspaces_seconds = {N(seed.WorkspacesSeconds)}");
		}

		text.AppendLine().AppendLine($"[{prefix}state]");
		text.AppendLine($"main_seq = {report.MainSeq}");
		text.AppendLine($"tree = {S(report.Tree)}");
		text.AppendLine($"open_workspaces = {report.OpenWorkspaces}");
		text.AppendLine($"closed_workspaces = {report.ClosedWorkspaces}");

		text.AppendLine().AppendLine($"[{prefix}load]");
		text.AppendLine($"seconds = {N(report.LoadSeconds)}");
		text.AppendLine($"landings = {report.Landings}");
		text.AppendLine($"landings_per_second = {N(report.LoadSeconds > 0 ? report.Landings / report.LoadSeconds : 0)}");
		if (report.Background is { } background)
		{
			text.AppendLine().AppendLine($"[{prefix}background]");
			text.AppendLine($"statements = {background.Statements}");
			text.AppendLine($"blob_calls = {background.BlobCalls}");
			text.AppendLine($"statements_per_landing = {N(background.Landings > 0 ? (double)background.Statements / background.Landings : 0)}");
			text.AppendLine($"blob_calls_per_landing = {N(background.Landings > 0 ? (double)background.BlobCalls / background.Landings : 0)}");
		}

		foreach (var operation in Ordered(report.Operations))
		{
			text.AppendLine().AppendLine($"[[{prefix}operation]]");
			text.AppendLine($"name = {S(operation.Name)}");
			text.AppendLine($"count = {operation.Count}");
			text.AppendLine($"errors = {operation.Errors}");
			text.AppendLine($"per_second = {N(operation.Throughput)}");
			text.AppendLine($"p50_ms = {N(operation.P50)}");
			text.AppendLine($"p95_ms = {N(operation.P95)}");
			text.AppendLine($"p99_ms = {N(operation.P99)}");
			text.AppendLine($"max_ms = {N(operation.Max)}");
			text.AppendLine($"requests = {N(operation.Requests)}");
			if (operation.Statements is { } statements)
			{
				text.AppendLine($"statements = {N(statements)}");
			}

			if (operation.BlobCalls is { } blobs)
			{
				text.AppendLine($"blob_calls = {N(blobs)}");
			}
		}
	}

	// ----- Markdown -----

	private static string Cell(double? value, string format = "0.#") => value is { } number ? number.ToString(format, CultureInfo.InvariantCulture) : "-";

	private static void Table(StringBuilder text, RunReport report)
	{
		text.AppendLine("| Operation | Count | Per second | p50 ms | p95 ms | p99 ms | Requests | Statements | Blob calls | Errors |");
		text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
		foreach (var operation in Ordered(report.Operations))
		{
			text.AppendLine($"| {operation.Name} | {operation.Count} | {Cell(operation.Throughput)} | {Cell(operation.P50, "0.##")} | {Cell(operation.P95, "0.##")} | " +
				$"{Cell(operation.P99, "0.##")} | {Cell(operation.Requests)} | {Cell(operation.Statements)} | {Cell(operation.BlobCalls)} | {operation.Errors} |");
		}
	}

	private static string Describe(Scenario scenario) =>
		string.Join(", ", Scenario.Parameters.Select(parameter => $"{parameter.Name} {Parameter.Format(parameter.Get(scenario))}"));

	private static void Summary(StringBuilder text, RunReport report)
	{
		text.AppendLine($"- Scenario `{report.Scenario.Name}`: {Describe(report.Scenario)}.");
		text.AppendLine($"- Server: {report.Server}, version {report.Version}.");
		if (report.Seed is { } seed)
		{
			text.AppendLine($"- Seeded in {N(seed.Seconds)} s (files {N(seed.FilesSeconds)} s, history {N(seed.HistorySeconds)} s, workspaces {N(seed.WorkspacesSeconds)} s).");
		}
		else if (!report.Seeded)
		{
			text.AppendLine("- Ran on a state seeded earlier.");
		}

		text.AppendLine($"- State at the end: mainline at seq {report.MainSeq} (tree `{report.Tree[..Math.Min(12, report.Tree.Length)]}`), " +
			$"{report.OpenWorkspaces} open and {report.ClosedWorkspaces} closed workspaces.");
		text.AppendLine($"- Agents: {report.Landings} landings in {N(report.LoadSeconds)} s ({N(report.LoadSeconds > 0 ? report.Landings / report.LoadSeconds : 0)} per second), {report.Errors} error(s).");
		if (report.Background is { Landings: > 0 } background)
		{
			text.AppendLine($"- Background reconciliation while the agents ran: {N((double)background.Statements / background.Landings)} statements and " +
				$"{N((double)background.BlobCalls / background.Landings)} blob calls per landing.");
		}
	}

	private const string Legend =
		"Requests, statements and blob calls are the average for one execution of the operation; statements (PostgreSQL) and blob calls are counted only in a server the harness hosts.";

	public static string Markdown(RunReport report)
	{
		var text = new StringBuilder();
		text.AppendLine($"# Bassia load report: {report.Scenario.Name}").AppendLine();
		Summary(text, report);
		text.AppendLine();
		Table(text, report);
		text.AppendLine().AppendLine(Legend);
		return text.ToString();
	}

	public static string Markdown(SweepReport sweep)
	{
		var text = new StringBuilder();
		var values = sweep.Runs.Select(run => Parameter.Format(run.Value)).ToList();
		text.AppendLine($"# Bassia load report: sweep of `{sweep.Parameter.Name}` over {string.Join(", ", values)}").AppendLine();
		text.AppendLine($"`{sweep.Parameter.Name}`: {sweep.Parameter.Meaning}.").AppendLine();
		text.AppendLine($"## Growth from {values[0]} to {values[^1]}").AppendLine();
		text.AppendLine(sweep.Parameter.State
			? $"The parameter sizes the state, not the work asked for: an operation marked **grows** costs more only because the state is larger " +
				$"(statements, blob calls or requests by {N(SweepReport.CountFactor)}x or more, or median latency by {N(SweepReport.LatencyFactor)}x or more)."
			: "The parameter changes the load applied, so growth is expected and nothing is marked.");
		text.AppendLine();
		text.AppendLine("| Operation | p50 | Requests | Statements | Blob calls | |");
		text.AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");
		string Times(double? ratio) => ratio is not { } value ? "-" : double.IsInfinity(value) ? "from 0" : $"{value.ToString("0.##", CultureInfo.InvariantCulture)}x";
		var order = Ordered(sweep.Runs[^1].Run.Operations).Select(operation => operation.Name).ToList();
		foreach (var growth in sweep.Growth().OrderBy(growth => order.IndexOf(growth.Operation)))
		{
			text.AppendLine($"| {growth.Operation} | {Times(growth.Latency)} | {Times(growth.Requests)} | {Times(growth.Statements)} | {Times(growth.BlobCalls)} | {(growth.Grows ? "**grows**" : "")} |");
		}

		foreach (var (value, run) in sweep.Runs)
		{
			text.AppendLine().AppendLine($"## {sweep.Parameter.Name} = {Parameter.Format(value)}").AppendLine();
			Summary(text, run);
			text.AppendLine();
			Table(text, run);
		}

		text.AppendLine().AppendLine(Legend);
		return text.ToString();
	}

	// ----- reading and comparing -----

	/// <summary>The operations of a stored report, by run: one run named "" for a single run, one per value for a sweep.</summary>
	public static Dictionary<string, Dictionary<string, Dictionary<string, double>>> Read(string file)
	{
		var text = File.ReadAllText(file);
		if (!text.Contains(Marker, StringComparison.Ordinal))
		{
			throw new ArgumentException($"'{file}' is not a bassia load report.");
		}

		var table = TomlSerializer.Deserialize<TomlTable>(text)!;
		var runs = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.Ordinal);
		static Dictionary<string, Dictionary<string, double>> OperationsOf(TomlTable run)
		{
			var operations = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
			if (run.TryGetValue("operation", out var value) && value is TomlTableArray array)
			{
				foreach (var item in array)
				{
					operations[(string)item["name"]] = item.Where(pair => pair.Value is long or double)
						.ToDictionary(pair => pair.Key, pair => Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
				}
			}

			return operations;
		}

		if (table.TryGetValue("run", out var sweep) && sweep is TomlTableArray sweepRuns)
		{
			var parameter = (string)table["parameter"];
			foreach (var run in sweepRuns)
			{
				runs[$"{parameter} = {Parameter.Format(Convert.ToDouble(run["value"], CultureInfo.InvariantCulture))}"] = OperationsOf(run);
			}
		}
		else
		{
			runs[""] = OperationsOf(table);
		}

		return runs;
	}

	private static readonly (string Key, string Label)[] Compared =
		[("p50_ms", "p50 ms"), ("p95_ms", "p95 ms"), ("requests", "requests"), ("statements", "statements"), ("blob_calls", "blob calls")];

	/// <summary>Sub-millisecond medians are noise: a latency counts as changed only when one side reaches this.</summary>
	private const double LatencyFloorMilliseconds = 2;

	/// <summary>The measures that changed by more than <paramref name="threshold"/> percent between two reports, for the runs and operations both hold.</summary>
	public static IReadOnlyList<Difference> Compare(string before, string after, double threshold)
	{
		var (old, current) = (Read(before), Read(after));
		var differences = new List<Difference>();
		foreach (var (run, operations) in old)
		{
			if (!current.TryGetValue(run, out var others))
			{
				continue;
			}

			foreach (var (name, measures) in operations)
			{
				if (!others.TryGetValue(name, out var other))
				{
					continue;
				}

				foreach (var (key, label) in Compared)
				{
					if (!measures.TryGetValue(key, out var a) || !other.TryGetValue(key, out var b))
					{
						continue;
					}

					if (key.EndsWith("_ms", StringComparison.Ordinal) && Math.Max(a, b) < LatencyFloorMilliseconds)
					{
						continue;
					}

					var difference = new Difference(run, name, label, a, b);
					if (Math.Abs(difference.Percent) > threshold)
					{
						differences.Add(difference);
					}
				}
			}
		}

		return differences;
	}

	public static string Markdown(IReadOnlyList<Difference> differences, double threshold)
	{
		var text = new StringBuilder();
		if (differences.Count == 0)
		{
			return $"No operation changed by more than {N(threshold)}%.\n";
		}

		string Row(Difference difference) =>
			$"| {(difference.Run.Length == 0 ? "" : difference.Run + " | ")}{difference.Operation} | {difference.Measure} | {N(difference.Before)} | {N(difference.After)} | " +
			$"{(double.IsInfinity(difference.Percent) ? "new" : difference.Percent.ToString("+0;-0", CultureInfo.InvariantCulture) + "%")} |";
		var runs = differences.Any(difference => difference.Run.Length > 0);
		foreach (var (title, items) in new[]
		{
			("Slower or more expensive", differences.Where(difference => difference.Percent > 0).ToList()),
			("Faster or cheaper", differences.Where(difference => difference.Percent < 0).ToList())
		})
		{
			if (items.Count == 0)
			{
				continue;
			}

			text.AppendLine($"## {title} (by more than {N(threshold)}%)").AppendLine();
			text.AppendLine($"| {(runs ? "Run | " : "")}Operation | Measure | Before | After | Change |");
			text.AppendLine($"| {(runs ? "--- | " : "")}--- | --- | ---: | ---: | ---: |");
			foreach (var item in items)
			{
				text.AppendLine(Row(item));
			}

			text.AppendLine();
		}

		return text.ToString();
	}
}

/// <summary>The names of the measured operations.</summary>
internal static class Operations
{
	public const string WorkspaceCreate = "workspace create";
	public const string LeaseAcquire = "lease acquire";
	public const string LeaseRelease = "lease release";
	public const string Snapshot = "snapshot";
	public const string ReconcileAfterSnapshot = "reconciled after a snapshot";
	public const string Sync = "sync";
	public const string Land = "land";
	public const string WorkspaceClose = "workspace close";
	public const string ReconcileScan = "reconcile scan after a landing";
	public const string ReconcileCatchUp = "reconcile catch-up at start";
	public const string ConflictOverview = "conflict overview";
	public const string WorkspaceList = "workspace list (open)";
	public const string WorkspaceResolve = "workspace resolve by name";
	public const string MainAtSeq = "mainline at seq 1";
	public const string LogPage = "mainline log page (middle)";
	public const string LogHead = "mainline log page (head)";
	public const string GarbageCollection = "garbage collection (dry run)";
}
