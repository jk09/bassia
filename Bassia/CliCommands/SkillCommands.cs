namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Skills;

/// <summary><c>bassia skill list|show</c>: the meta-repo's reusable instructions, for any agent that drives bassia.</summary>
internal static class SkillCommands
{
	// ----- bassia skill list | show -----

	public static Task<int> SkillListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var skills = SkillStore.Load(monorepo.Root);
		var directory = SkillStore.DirectoryOf(monorepo.Root);
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command,
			skills.Count == 0
				? $"No skills. Add one as {Path.Combine(directory, "<name>", SkillStore.FileName)} and commit it to the meta-repo."
				: $"{skills.Count} skill(s) in '{directory}'.",
			new Dictionary<string, object?>
			{
				["path"] = directory,
				["table"] = skills.Count == 0 ? null : new TomlText(AsciiTable.Render(["SKILL", "DESCRIPTION"],
					skills.Select(skill => (IReadOnlyList<string>)[skill.Name, skill.Description]), 120)),
				["skill"] = skills.Select(skill => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["name"] = skill.Name,
					["description"] = skill.Description,
					["path"] = skill.Path
				}).ToList()
			}));
	}

	public static Task<int> SkillShowAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var skills = SkillStore.Load(monorepo.Root);
		var name = invocation.Require("name");
		var skill = SkillStore.Find(skills, name)
			?? throw new MonorepoException($"Unknown skill '{name}'. {(skills.Count == 0 ? "This monorepo has no skills." : $"Known skills: {string.Join(", ", skills.Select(candidate => candidate.Name))}.")}");
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, skill.Description, new Dictionary<string, object?>
		{
			["name"] = skill.Name,
			["description"] = skill.Description,
			["path"] = skill.Path,
			["body"] = new TomlText(skill.Body)
		}));
	}
}
