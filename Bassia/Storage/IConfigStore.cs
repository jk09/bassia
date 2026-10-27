namespace Bassia.Storage;

/// <summary>
/// Where the monorepo's settings live, layer by layer (see <see cref="ConfigLayer"/>): the monorepo layer is shared and
/// versioned with the meta-repo, the user layer is each user's own and never shared. <see cref="LocalConfigStore"/>
/// keeps them in <c>.bassia/config.toml</c> and <c>.bassia/config.user.toml</c>.
/// </summary>
internal interface IConfigStore
{
	/// <summary>Where a layer lives, as the backend names it; shown to users, never parsed.</summary>
	string LocationOf(ConfigLayer layer);

	/// <summary>Both layers as they are now.</summary>
	ConfigSnapshot Read();

	/// <summary>
	/// Writes <paramref name="value"/> (already checked, see <see cref="ConfigFile.Normalize"/>) for <paramref name="key"/> in
	/// <paramref name="layer"/>; returns the meta-repo revision recording a change of the monorepo layer, else null.
	/// </summary>
	Task<string?> SetAsync(ConfigKey key, string value, ConfigLayer layer);

	/// <summary>Removes <paramref name="key"/> from <paramref name="layer"/>: whether it was set, and the revision recording it.</summary>
	Task<(bool Removed, string? Revision)> UnsetAsync(ConfigKey key, ConfigLayer layer);
}
