namespace Bassia.Integration;

using System.Text;

/// <summary>
/// The structural merge: <see href="https://github.com/Ataraxy-Labs/weave">weave</see>'s entity-level merge driver,
/// which parses the three sides with tree-sitter and merges functions, classes and keys instead of lines. Two runs
/// that add or change different functions of one file conflict for git but not for weave; two runs that change the
/// same function still conflict for both. It is deterministic like git's own merge, so the triage chains it like a
/// syntactic merge, and it sits between git (tried first) and the resolver (only what weave cannot merge either).
/// <para>
/// Nothing in a component repo is changed to use it: the driver and the attributes that select it are passed to git
/// with <c>-c</c> for the one command. A component's own <c>.gitattributes</c> still takes precedence over them.
/// </para>
/// </summary>
internal sealed class StructuralMerge
{
	/// <summary>The value of <c>[integration] weave</c> or <c>-weave</c> that disables the structural merge.</summary>
	public const string Off = "off";

	/// <summary>The name of the merge driver in git's configuration, as <c>weave setup</c> names it.</summary>
	public const string DriverName = "weave";

	/// <summary>The stderr prefix of a semantic warning on a merge weave did complete ("clean with warnings").</summary>
	public const string WarningPrefix = "weave-warning: ";

	/// <summary>The placeholders git hands a merge driver: ancestor, ours (also the output), theirs, marker size, path.</summary>
	private const string Placeholders = "%O %A %B %L %P";

	/// <summary>
	/// The file types weave merges by entity, as <c>weave setup</c> (0.5.4) derives them from its parsers; it leaves
	/// out the ones weave declines (Vue, Svelte, ERB, Haskell). Any other file keeps git's line merge.
	/// </summary>
	public static readonly IReadOnlyList<string> Extensions =
	[
		"axml", "bsl", "c", "cc", "cjs", "clj", "cljc", "cljs", "cls", "cpp", "cs", "csproj", "csv", "cts", "cxx", "d", "dart",
		"ddl", "di", "edn", "elm", "es6", "ex", "exs", "f", "f03", "f08", "f90", "f95", "fish", "for", "fsproj", "go", "h", "hcl",
		"hh", "hpp", "hxx", "inc", "java", "js", "json", "jsx", "kojo", "kt", "kts", "latex", "lua", "md", "mdx", "mill", "mjs",
		"ml", "mli", "module", "mts", "nix", "nuspec", "osl", "pgsql", "php", "phtml", "pl", "plist", "pm", "props", "psql", "py",
		"pyi", "rb", "resx", "rs", "sbt", "sc", "scala", "sh", "sql", "sty", "svg", "swift", "t", "targets", "tex", "tf", "tfvars",
		"toml", "ts", "tsv", "tsx", "vbproj", "xaml", "xhtml", "xml", "yaml", "yml", "zig"
	];

	/// <param name="command">The driver as configured: an executable (git appends the placeholders) or a full command with them.</param>
	/// <param name="attributesFile">The attributes file that assigns the driver to <see cref="Extensions"/>.</param>
	private StructuralMerge(string command, string attributesFile)
	{
		Command = command;
		AttributesFile = attributesFile;
	}

	/// <summary>The driver command as configured, e.g. <c>weave-driver</c>; what the records name.</summary>
	public string Command { get; }

	public string AttributesFile { get; }

	/// <summary>The command git runs per file: the configured one, with git's placeholders appended unless it has its own.</summary>
	public string DriverLine => Command.Contains("%A", StringComparison.Ordinal) ? Command : $"{Command} {Placeholders}";

	/// <summary>The options that make one git command (<c>merge</c>, <c>merge-tree</c>) merge with the structural driver.</summary>
	public IReadOnlyList<string> GitOptions =>
	[
		"-c", $"merge.{DriverName}.name=Entity-level semantic merge (weave)",
		"-c", $"merge.{DriverName}.driver={DriverLine}",
		"-c", $"core.attributesFile={AttributesFile}"
	];

	/// <summary>
	/// The structural merge of the monorepo, or null with the reason: <paramref name="command"/> (from <c>-weave</c>)
	/// or else <c>[integration] weave</c> names the driver, <c>off</c> disables it, and a driver that is not installed
	/// leaves the integration exactly as it was without one.
	/// </summary>
	/// <returns>The merge, and how to describe the setting: the driver command, <c>off</c>, or why it is unavailable.</returns>
	public static (StructuralMerge? Merge, string Status) Resolve(Monorepo monorepo, string? command = null)
	{
		command = string.IsNullOrWhiteSpace(command) ? monorepo.StructuralDriver : command.Trim();
		if (command.Equals(Off, StringComparison.OrdinalIgnoreCase))
		{
			return (null, Off);
		}

		if (!IsInstalled(command))
		{
			return (null, $"unavailable: '{Executable(command)}' was not found; install weave (https://github.com/Ataraxy-Labs/weave) " +
				"or set integration.weave to its weave-driver");
		}

		var attributesFile = Path.Combine(monorepo.WorkspaceDir, ".structural-merge.gitattributes");
		var attributes = string.Concat(Extensions.Select(extension => $"*.{extension} merge={DriverName}\n"));
		Directory.CreateDirectory(monorepo.WorkspaceDir);
		if (!File.Exists(attributesFile) || File.ReadAllText(attributesFile) != attributes)
		{
			File.WriteAllText(attributesFile, attributes);
		}

		return (new StructuralMerge(command, attributesFile), command);
	}

	/// <summary>The <c>weave-warning:</c> payloads (JSON) among a git command's stderr, in order and without repeats.</summary>
	public static IReadOnlyList<string> Warnings(string error) =>
		error.Replace("\r", "").Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.StartsWith(WarningPrefix, StringComparison.Ordinal))
			.Select(line => line[WarningPrefix.Length..].Trim())
			.Distinct(StringComparer.Ordinal)
			.ToList();

	/// <summary>
	/// Whether a line is weave's pointer to <c>weave explain</c>, which it adds to a conflicted file outside the
	/// conflict boxes; left in a resolved file, it would be committed.
	/// </summary>
	public static bool IsAnnotation(string line) => line.Contains("weave: run 'weave explain", StringComparison.Ordinal);

	/// <summary>The executable of a command line: its first word, or its first quoted string.</summary>
	internal static string Executable(string command)
	{
		command = command.Trim();
		if (command.Length > 0 && command[0] is '"' or '\'')
		{
			var end = command.IndexOf(command[0], 1);
			return end > 0 ? command[1..end] : command[1..];
		}

		var space = command.IndexOfAny([' ', '\t']);
		return space < 0 ? command : command[..space];
	}

	/// <summary>Whether the command's executable exists: as a path, or on <c>PATH</c> (with <c>PATHEXT</c> on Windows).</summary>
	internal static bool IsInstalled(string command)
	{
		var executable = Executable(command);
		if (executable.Length == 0)
		{
			return false;
		}

		var extensions = OperatingSystem.IsWindows()
			? new[] { "" }.Concat((Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)).ToList()
			: [""];
		if (executable.Contains('/') || executable.Contains('\\'))
		{
			return extensions.Any(extension => File.Exists(executable + extension));
		}

		return (Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(directory => extensions.Any(extension => File.Exists(Path.Combine(directory.Trim('"'), executable + extension))));
	}

	/// <summary>How the brief explains weave's conflict markers to the resolver.</summary>
	internal static string MarkerHelp()
	{
		var help = new StringBuilder();
		help.AppendLine("Weave's conflict boxes name the entity they are about, e.g. `<<<<<<< ours — function `process` (T, confidence: high)`, " +
			"and carry a comment line `refused_by: ...` saying why weave did not merge it; they have no common-ancestor section " +
			"(the diffs below show what each side changed). A conflicted file can also end with a comment line " +
			"`weave: run 'weave explain <file>' ...`. Remove all of these lines together with the markers.");
		return help.ToString().TrimEnd();
	}
}
