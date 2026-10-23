namespace Bassia.Locks;

using Bassia.Split;

/// <summary>Whether a hold covers one file, or a folder with everything in it.</summary>
internal enum LockKind { File, Folder }

/// <summary>
/// What one hold covers in one component: a file, or a folder and everything below it. <see cref="Path"/> is relative to
/// the component's root with <c>/</c> separators; the empty path is the whole component (only an explicit claim holds
/// that).
/// </summary>
internal sealed record LockKey(string Component, string Path, LockKind Kind)
{
	/// <summary>Whether two keys cover a common file: the same file or folder, or a folder and anything inside it.</summary>
	public bool Overlaps(LockKey other) =>
		Component == other.Component && (Covers(other.Path) || other.Covers(Path));

	/// <summary>Whether <paramref name="path"/> (a file or folder of the same component) is this key or inside it.</summary>
	public bool Covers(string path) =>
		Path == path || (Kind == LockKind.Folder && (Path.Length == 0 || path.StartsWith(Path + "/", StringComparison.Ordinal)));

	/// <summary>The key as the run folder shows it: <c>app/src/x.cs</c>, a folder with a trailing slash (<c>app/src/</c>).</summary>
	public string Display => Kind == LockKind.Folder ? $"{Component}/{(Path.Length == 0 ? "" : Path + "/")}" : $"{Component}/{Path}";

	public static string KindName(LockKind kind) => kind == LockKind.Folder ? "folder" : "file";

	public static LockKind ParseKind(string text) => text == "folder" ? LockKind.Folder : LockKind.File;
}

/// <summary>What the integration does with a result that changed a path another run held (<c>lock.conflict</c>).</summary>
internal enum LockConflictPolicy
{
	/// <summary>The resolver reviews it, even where git or weave would merge it cleanly (the default).</summary>
	Resolver,

	/// <summary>It is left for a human.</summary>
	Manual,

	/// <summary>It merges like any other result; the conflict is only shown.</summary>
	Merge
}

/// <summary>
/// The paths of one component a run holds while its agent changes them: the <c>lock.files</c> and <c>lock.folders</c>
/// patterns configured for the component (<c>lock.component.&lt;c&gt;.*</c>, then <c>lock.*</c>) plus the run's own
/// <c>-lock-files</c> / <c>-lock-folders</c>. Each source keeps its own pattern list, so a run's exclusions (<c>!x</c>) can
/// narrow only what the run itself adds, never switch the monorepo's locks off.
/// </summary>
internal sealed class LockFilter
{
	private readonly IReadOnlyList<PathPatterns> files;
	private readonly IReadOnlyList<PathPatterns> folders;

	private LockFilter(IReadOnlyList<PathPatterns> files, IReadOnlyList<PathPatterns> folders)
	{
		this.files = files.Where(patterns => !patterns.IsEmpty).ToList();
		this.folders = folders.Where(patterns => !patterns.IsEmpty).ToList();
	}

	public static LockFilter None { get; } = new([], []);

	public bool IsEmpty => files.Count == 0 && folders.Count == 0;

	/// <summary>Every pattern, for showing the filter: the file patterns and the folder patterns.</summary>
	public (IReadOnlyList<string> Files, IReadOnlyList<string> Folders) Texts =>
		(files.SelectMany(patterns => patterns.Texts).ToList(), folders.SelectMany(patterns => patterns.Texts).ToList());

	/// <summary>
	/// The key a change to <paramref name="path"/> (a file, relative to the component's root) is held as: the nearest
	/// folder above it that a folder pattern names (<c>src/*</c> names <c>src/api</c>; <c>**</c> names every folder);
	/// otherwise the file itself when a file pattern matches it or a folder pattern covers it without naming one of its
	/// folders (a file at the component's root under <c>**</c>); otherwise null - the file is not held.
	/// </summary>
	public LockKey? KeyFor(string component, string path)
	{
		var segments = path.Split('/');
		for (var depth = segments.Length - 1; depth >= 1; depth--)
		{
			var folder = string.Join('/', segments.Take(depth));
			if (folders.Any(patterns => patterns.Names(folder)))
			{
				return new LockKey(component, folder, LockKind.Folder);
			}
		}

		return files.Any(patterns => patterns.Matches(path)) || folders.Any(patterns => patterns.Matches(path))
			? new LockKey(component, path, LockKind.File)
			: null;
	}

	/// <summary>The filter of <paramref name="component"/> for a run with the given own patterns (null: none).</summary>
	public static LockFilter For(Monorepo monorepo, string component, RunLockPatterns? run = null)
	{
		var configFiles = Patterns(monorepo, component, "files");
		var configFolders = Patterns(monorepo, component, "folders");
		return new LockFilter(
			[configFiles, Build(run?.Files(component) ?? [], "-lock-files")],
			[configFolders, Build(run?.Folders(component) ?? [], "-lock-folders")]);
	}

	/// <summary>The lock policy of a component: <c>lock.component.&lt;c&gt;.conflict</c>, then <c>lock.conflict</c>.</summary>
	public static LockConflictPolicy ConflictPolicy(ConfigSnapshot config, string component) =>
		config.Resolve(ConfigFile.ComponentKey("lock", component, "conflict")).Value.Trim().ToLowerInvariant() switch
		{
			"manual" => LockConflictPolicy.Manual,
			"merge" => LockConflictPolicy.Merge,
			_ => LockConflictPolicy.Resolver
		};

	/// <summary>How long a hold lasts without its run renewing it (<c>lock.ttl</c>).</summary>
	public static TimeSpan Ttl(ConfigSnapshot config) =>
		TimeSpan.FromSeconds(int.TryParse(config.Value("lock.ttl").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 120);

	private static PathPatterns Patterns(Monorepo monorepo, string component, string setting)
	{
		var value = monorepo.Config.Resolve(ConfigFile.ComponentKey("lock", component, setting));
		return Build(value.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), value.SetAs);
	}

	private static PathPatterns Build(IReadOnlyList<string> patterns, string source)
	{
		try
		{
			return patterns.Count == 0 ? PathPatterns.Empty : new PathPatterns(patterns);
		}
		catch (ArgumentException ex)
		{
			throw new MonorepoException($"{source}: {ex.Message}");
		}
	}
}

/// <summary>
/// A run's own lock patterns, as <c>run start -lock-files</c> and <c>-lock-folders</c> give them: comma-separated
/// patterns, each for every component of the run or, with a <c>&lt;component&gt;:</c> prefix, for that one only.
/// </summary>
internal sealed class RunLockPatterns
{
	private readonly IReadOnlyList<(string? Component, string Pattern)> files;
	private readonly IReadOnlyList<(string? Component, string Pattern)> folders;

	private RunLockPatterns(IReadOnlyList<(string?, string)> files, IReadOnlyList<(string?, string)> folders)
	{
		this.files = files;
		this.folders = folders;
	}

	public bool IsEmpty => files.Count == 0 && folders.Count == 0;

	public IReadOnlyList<string> Files(string component) => For(files, component);

	public IReadOnlyList<string> Folders(string component) => For(folders, component);

	/// <summary>
	/// Parses the switches' values, or the values a run record keeps (null or empty: none). A <c>&lt;component&gt;:</c>
	/// prefix must name one of <paramref name="components"/>; every pattern must be valid.
	/// </summary>
	public static RunLockPatterns Parse(string? lockFiles, string? lockFolders, IReadOnlyCollection<string> components)
	{
		var parsed = new RunLockPatterns(Entries("-lock-files", lockFiles, components), Entries("-lock-folders", lockFolders, components));
		foreach (var component in components)
		{
			Check(parsed.Files(component), "-lock-files");
			Check(parsed.Folders(component), "-lock-folders");
		}

		return parsed;

		static void Check(IReadOnlyList<string> patterns, string option)
		{
			try
			{
				_ = patterns.Count == 0 ? PathPatterns.Empty : new PathPatterns(patterns);
			}
			catch (ArgumentException ex)
			{
				throw new MonorepoException($"{option}: {ex.Message}");
			}
		}
	}

	private static List<(string?, string)> Entries(string option, string? value, IReadOnlyCollection<string> components)
	{
		var entries = new List<(string?, string)>();
		foreach (var item in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var colon = item.IndexOf(':');
			if (colon < 0)
			{
				entries.Add((null, item));
				continue;
			}

			var component = item[..colon].Trim();
			if (!components.Contains(component))
			{
				throw new MonorepoException($"{option} entry '{item}' names '{component}', which is not a component of the run; " +
					$"prefix a pattern with one of {string.Join(", ", components)} and a colon, or give it without a prefix for every component.");
			}

			entries.Add((component, item[(colon + 1)..].Trim()));
		}

		return entries;
	}

	private static IReadOnlyList<string> For(IReadOnlyList<(string? Component, string Pattern)> entries, string component) =>
		entries.Where(entry => entry.Component is null || entry.Component == component).Select(entry => entry.Pattern).ToList();
}
