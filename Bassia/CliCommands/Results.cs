namespace Bassia.CliCommands;

using System.Globalization;
using Bassia.Cli;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>How commands describe the model in their TOML results: one vocabulary of snake_case keys everywhere.</summary>
internal static class Results
{
	public static int Ok(Invocation invocation, string message, Dictionary<string, object?>? data = null) =>
		ProgramCli.WriteResult(true, invocation.Command, message, data);

	public static int Fail(Invocation invocation, string message, Dictionary<string, object?>? data = null) =>
		ProgramCli.WriteResult(false, invocation.Command, message, data);

	public static string Time(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

	public static string? Time(DateTimeOffset? time) => time is { } value ? Time(value) : null;

	public static async Task<Dictionary<string, object?>> WorkspaceAsync(Repository repo, WorkspaceState state, CancellationToken ct = default)
	{
		var record = state.Record;
		var working = await repo.Objects.ReadCommitAsync(record.Working, ct);
		var data = new Dictionary<string, object?>
		{
			["id"] = record.Id,
			["name"] = record.Name,
			["status"] = record.Status,
			["parent"] = record.Parent,
			["base_seq"] = record.BaseSeq,
			["base"] = record.Base.Hex,
			["working"] = record.Working.Hex,
			["change_id"] = working.ChangeId.Hex,
			["intent"] = working.Intent.Length == 0 ? null : working.Intent,
			["owner"] = record.Owner,
			["created"] = Time(record.Created),
			["closed"] = Time(record.Closed),
			["path"] = record.Path,
			["host"] = record.Host,
			["replay_of"] = record.ReplayOf,
			["operations"] = state.Operation.Seq + 1
		};
		if (record.Run is { } run)
		{
			data["run"] = Run(run);
		}

		if (record.LastLand is { } land)
		{
			data["last_land"] = new Dictionary<string, object?>
			{
				["outcome"] = land.Outcome,
				["time"] = Time(land.Time),
				["seq"] = land.Seq,
				["commit"] = land.Commit?.Hex,
				["conflicts"] = land.Conflicts.ToList(),
				["message"] = land.Message
			};
		}

		return data;
	}

	public static Dictionary<string, object?> Run(RunRecord run) => new()
	{
		["run_id"] = run.RunId,
		["status"] = run.Status == RunStatus.Running && !Agents.AgentRunner.IsAlive(run) ? "lost" : run.Status,
		["agent"] = run.Agent,
		["model"] = run.Model,
		["prompt"] = run.Prompt,
		["command"] = run.Command,
		["started"] = Time(run.Started),
		["finished"] = Time(run.Finished),
		["exit_code"] = run.ExitCode,
		["host"] = run.Host,
		["last_line"] = run.LastLine
	};

	public static Dictionary<string, object?> Land(LandResult result) => new()
	{
		["outcome"] = result.Outcome,
		["seq"] = result.Seq,
		["commit"] = result.Commit?.Hex,
		["main_seq"] = result.MainSeq,
		["main_commit"] = result.MainCommit.Hex,
		["touched"] = result.Touched,
		["attempts"] = result.Attempts,
		["merged"] = result.Merged.ToList(),
		["warnings"] = result.Warnings.ToList(),
		["conflict"] = result.Conflicts.Select(conflict => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
		{
			["path"] = conflict.Path,
			["case"] = conflict.Case,
			["policy"] = conflict.Policy,
			["detail"] = conflict.Detail
		}).ToList()
	};

	public static IReadOnlyDictionary<string, object?> Diff(DiffRecord record) => new Dictionary<string, object?>
	{
		["path"] = record.Path,
		["status"] = record.Status,
		["old_path"] = record.OldPath,
		["kind"] = (record.New ?? record.Old)?.Kind.ToString().ToLowerInvariant(),
		["old_size"] = record.Old?.Size,
		["new_size"] = record.New?.Size,
		["old_id"] = record.Old?.Id.Hex,
		["new_id"] = record.New?.Id.Hex,
		["old_executable"] = record.Old?.Executable,
		["new_executable"] = record.New?.Executable,
		["similarity"] = record.Similarity
	};

	public static Dictionary<string, object?> Operation(OperationEntry entry) => new()
	{
		["id"] = entry.Id.Hex,
		["seq"] = entry.Operation.Seq,
		["time"] = Time(entry.Operation.Time),
		["description"] = entry.Operation.Description,
		["agent"] = entry.Operation.Agent,
		["run"] = entry.Operation.Run,
		["workspace"] = entry.Operation.Workspace
	};

	public static Dictionary<string, object?> Lease(Lease lease) => new()
	{
		["id"] = lease.Id,
		["prefix"] = lease.Prefix,
		["mode"] = lease.Mode.ToString().ToLowerInvariant(),
		["owner"] = lease.Owner,
		["run"] = lease.Run,
		["token"] = lease.Token,
		["granted"] = Time(lease.Granted),
		["expires"] = Time(lease.Expires)
	};

	public static List<IReadOnlyDictionary<string, object?>> Tables<T>(IEnumerable<T> items, Func<T, IReadOnlyDictionary<string, object?>> map) =>
		items.Select(map).ToList();

	/// <summary>A one-line summary of diff records: "3 added, 1 modified".</summary>
	public static string Summary(IReadOnlyList<DiffRecord> records) => records.Count == 0
		? "no changes"
		: string.Join(", ", records.GroupBy(record => record.Status).OrderBy(group => group.Key).Select(group => $"{group.Count()} {group.Key}"));

	public static string Short(string text, int length = 60)
	{
		var line = text.Split('\n')[0].Trim();
		return line.Length > length ? line[..(length - 3)] + "..." : line;
	}

	public static string Size(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
		< 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MiB",
		_ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GiB"
	};

	/// <summary>An ASCII table as multi-line text in a result.</summary>
	public static TomlText Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows) => new(AsciiTable.Render(headers, rows, 160));
}
