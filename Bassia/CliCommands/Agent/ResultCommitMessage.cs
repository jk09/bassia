namespace Bassia.CliCommands.Agent;

using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Message of the commit an agentic run makes in a component: a subject line rendered from the configured template,
/// a blank line, and a TOML record of the run and the component. The body is serialized, never templated, so it
/// always parses.
/// </summary>
internal static class ResultCommitMessage
{
	public const string TableName = "agentic_run";

	public static string Render(string subjectTemplate, RunMetadata metadata, ComponentRun component, string resultTag, string summary)
	{
		var subject = subjectTemplate
			.Replace("{run_id}", metadata.RunId)
			.Replace("{short_id}", RunMetadata.Key(metadata.RunId))
			.Replace("{summary}", summary)
			.Replace("{component}", component.Name)
			.Split('\n')[0].Trim();

		return $"{subject}\n\n{Body(metadata, component, resultTag, summary)}";
	}

	private static string Body(RunMetadata metadata, ComponentRun component, string resultTag, string summary)
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

		return TomlSerializer.Serialize(new TomlTable { [TableName] = run });
	}
}
