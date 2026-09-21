namespace Bassia.CliCommands.Agent;

using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Message of the commit an agentic run makes in a component, rendered from the configured template. The template's
/// <c>{metadata}</c> placeholder expands to a TOML record of the run and the component; that block is serialized,
/// never templated, so it always parses.
/// </summary>
internal static partial class ResultCommitMessage
{
	public const string TableName = "agentic_run";

	public static string Render(string template, RunMetadata metadata, ComponentRun component, string resultTag, string summary)
	{
		var message = Placeholder().Replace(template.Replace("\r\n", "\n"), match => match.Groups[1].Value switch
		{
			"run_id" => metadata.RunId,
			"short_id" => RunMetadata.ShortKey(metadata.RunId),
			"summary" => summary,
			"component" => component.Name,
			"metadata" => Metadata(metadata, component, resultTag, summary),
			_ => match.Value
		});

		return message.TrimEnd() + "\n";
	}

	private static string Metadata(RunMetadata metadata, ComponentRun component, string resultTag, string summary)
	{
		var run = new TomlTable
		{
			["id"] = metadata.RunId,
			["summary"] = summary,
			["command"] = metadata.Command,
			["select"] = metadata.Select,
			["created"] = metadata.Created
		};
		if (metadata.Finished is not null) run["finished"] = metadata.Finished;
		run["record_tag"] = RunMetadata.TagName(metadata.RunId, 0);

		run["component"] = new TomlTable
		{
			["name"] = component.Name,
			["commitish"] = component.CommitIsh,
			["base_commit"] = component.Commit,
			["branch"] = component.Branch,
			["tag"] = resultTag
		};

		return TomlSerializer.Serialize(new TomlTable { [TableName] = run }).TrimEnd();
	}

	// One pass, so a value that happens to contain "{metadata}" is not expanded again.
	[GeneratedRegex(@"\{(run_id|short_id|summary|component|metadata)\}")]
	private static partial Regex Placeholder();
}
