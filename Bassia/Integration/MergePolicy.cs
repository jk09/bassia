namespace Bassia.Integration;

using Bassia.Split;

/// <summary>What the integration does with a result weave merged, but with warnings (<c>merge.warnings</c>).</summary>
internal enum WarningsPolicy
{
	/// <summary>The resolver reviews it, like a conflict (the default).</summary>
	Resolver,

	/// <summary>It is left for a human.</summary>
	Manual,

	/// <summary>Weave's merge is taken as it is.</summary>
	Accept
}

/// <summary>
/// How the integration merges one component's results, from the <c>merge.*</c> settings (the component's own
/// <c>merge.component.&lt;name&gt;.*</c> first, then the global ones). It only narrows what reaches the resolver and
/// whether the base branch moves: git and weave always merge what they can merge cleanly.
/// </summary>
/// <param name="ManualSemantic">A result neither git nor weave can merge is left for a human instead of the resolver.</param>
/// <param name="ManualPaths">A result whose git conflicts touch any of these paths is left for a human.</param>
/// <param name="AutoAdvance">A completed integration fast-forwards the base branch itself.</param>
internal sealed record MergePolicy(bool ManualSemantic, WarningsPolicy Warnings, PathPatterns ManualPaths, bool AutoAdvance)
{
	public static MergePolicy Default { get; } = new(false, WarningsPolicy.Resolver, PathPatterns.Empty, false);

	public static MergePolicy For(ConfigSnapshot config, string component)
	{
		string Value(string setting) => config.Resolve(ConfigFile.ComponentKey(component, setting)).Value;
		var paths = Value("manual_paths").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		PathPatterns patterns;
		try
		{
			patterns = paths.Length == 0 ? PathPatterns.Empty : new PathPatterns(paths);
		}
		catch (ArgumentException ex)
		{
			throw new MonorepoException($"merge.manual_paths for '{component}': {ex.Message}");
		}

		return new MergePolicy(
			Value("semantic").Equals("manual", StringComparison.OrdinalIgnoreCase),
			IntegrationRecord.Parse<WarningsPolicy>(Value("warnings").Trim()),
			patterns,
			Value("advance").Equals("auto", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>Why a step with these git conflicts needs a human under this policy, or null.</summary>
	public string? ManualPathReason(IEnumerable<string> conflicts) =>
		ManualPaths.IsEmpty ? null
		: conflicts.Where(ManualPaths.Matches).ToList() is { Count: > 0 } hits ? $"conflicts in {string.Join(", ", hits)} match merge.manual_paths" : null;
}
