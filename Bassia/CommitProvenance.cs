namespace Bassia;

using Bassia.CliCommands.Agent;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Which agentic run a component commit belongs to, read from the TOML record Bassia writes into the messages of the
/// commits it makes: a run's result commit carries <c>[agentic_run]</c> (<see cref="ResultCommitMessage"/>), an
/// integration's merge commit <c>[integration]</c> with the run it merged. Commits made by hand carry neither.
/// </summary>
internal sealed record CommitProvenance(string Kind, string RunId, string? IntegrationId)
{
	/// <summary>The run's own result commit.</summary>
	public const string Result = "result";

	/// <summary>An integration's merge commit, bringing the run's result in.</summary>
	public const string Integration = "integration";

	private const string IntegrationTable = "integration";

	/// <summary>The provenance in a commit message body (the message without its subject line), or null when it has none.</summary>
	public static CommitProvenance? Parse(string body)
	{
		var table = Record(body, ResultCommitMessage.TableName) ?? Record(body, IntegrationTable);
		if (table is null)
		{
			return null;
		}

		if (table.TryGetValue(ResultCommitMessage.TableName, out var run) && run is TomlTable result
			&& result.TryGetValue("id", out var id) && id is string runId && runId.StartsWith(RunMetadata.RunIdPrefix, StringComparison.Ordinal))
		{
			return new CommitProvenance(Result, runId, null);
		}

		if (table.TryGetValue(IntegrationTable, out var merge) && merge is TomlTable integration
			&& integration.TryGetValue("run_id", out var merged) && merged is string mergedRun
			&& integration.TryGetValue("id", out var integrationId) && integrationId is string integrationName)
		{
			return new CommitProvenance(Integration, mergedRun, integrationName);
		}

		return null;
	}

	/// <summary>The body from its <c>[name]</c> header on, parsed; null when there is no such header or it does not parse.</summary>
	private static TomlTable? Record(string body, string name)
	{
		var header = $"[{name}]";
		var start = body.Replace("\r", "").Split('\n').Select((line, index) => (line, index)).FirstOrDefault(entry => entry.line.Trim() == header);
		if (start.line is null)
		{
			return null;
		}

		var text = string.Join('\n', body.Replace("\r", "").Split('\n').Skip(start.index));
		try
		{
			return TomlSerializer.Deserialize<TomlTable>(text);
		}
		catch (TomlException)
		{
			// Hand-written text that only looks like a record (or trailers after it): not Bassia's provenance.
			return null;
		}
	}
}
