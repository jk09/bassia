namespace Bassia.Core;

using System.Text;
using System.Text.RegularExpressions;
using Bassia.Split;
using Bassia.Storage;

/// <summary>What a dependency found in a component's code breaks.</summary>
internal enum CodeViolationKind
{
	/// <summary>A path of a referenced component that its <c>exports</c> do not cover.</summary>
	Private,

	/// <summary>A component reached through another component's nested reference (<c>lib/vendor/core/...</c>).</summary>
	Indirect,

	/// <summary>A path above the component's own root.</summary>
	Outside
}

/// <summary>
/// One dependency in <paramref name="Component"/>'s code that breaks its boundaries: in <paramref name="File"/> at
/// <paramref name="Line"/>, written <paramref name="Specifier"/>, reaching <paramref name="Target"/> (a component, or
/// null when the path leaves the monorepo's known components). <see cref="Key"/> leaves the line out, so moving the
/// line does not make it a new violation.
/// </summary>
internal sealed record CodeViolation(string Component, string File, int Line, string Specifier, CodeViolationKind Kind, string? Target, string Message)
{
	public string Key => $"{Component}\n{File}\n{Kind}\n{Target}\n{Specifier}";

	public string KindName => Kind.ToString().ToLowerInvariant();
}

/// <summary>The scan of one component at one commit: its code violations and which of its references its code uses.</summary>
internal sealed record CodeScan(string Component, string? Commit, int Files, int Dependencies, IReadOnlyList<CodeViolation> Violations,
	IReadOnlyList<string> UsedReferences, IReadOnlyList<string> UnusedReferences)
{
	/// <summary>The violations of this scan that <paramref name="before"/> did not have.</summary>
	public IReadOnlyList<CodeViolation> AddedOver(CodeScan? before)
	{
		var known = (before?.Violations ?? []).Select(violation => violation.Key).ToHashSet(StringComparer.Ordinal);
		return Violations.Where(violation => !known.Contains(violation.Key)).ToList();
	}
}

/// <summary>
/// Finds path-like dependencies in source files: relative JS/TS imports, quoted C/C++ includes, MSBuild items and
/// imports, <c>file:</c>/<c>link:</c> packages in <c>package.json</c>, and CMake <c>add_subdirectory</c>/<c>include</c>.
/// Namespace and module imports (C#, Python, Go, Java) name no folder, so they are not found.
/// </summary>
internal static class DependencyScanner
{
	/// <summary>Files larger than this are not read.</summary>
	public const int MaxFileBytes = 1 << 20;

	private static readonly string[] ScriptExtensions = [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".vue", ".svelte"];
	private static readonly string[] NativeExtensions = [".c", ".h", ".cc", ".hh", ".cpp", ".hpp", ".cxx", ".hxx", ".inl", ".m", ".mm"];
	private static readonly string[] MsBuildExtensions = [".csproj", ".fsproj", ".vbproj", ".vcxproj", ".proj", ".props", ".targets", ".sln"];

	/// <summary>The extensions a script import may leave out, tried when matching exports.</summary>
	public static readonly string[] ImpliedScriptExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".d.ts", "/index.ts", "/index.js"];

	private static readonly Regex Script = new(
		@"(?:\bimport\s+(?:[^'"";]*?\s+from\s+)?|\bexport\s+[^'"";]*?\s+from\s+|\bimport\s*\(\s*|\brequire\s*\(\s*)(['""])(?<spec>\.{1,2}/[^'""\r\n]*)\1",
		RegexOptions.CultureInvariant);

	private static readonly Regex Include = new(@"^[ \t]*#[ \t]*(?:include|import)[ \t]*""(?<spec>[^""\r\n]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant);

	private static readonly Regex MsBuild = new(
		@"<(?:ProjectReference|Import|Compile|Content|None|EmbeddedResource|ClInclude|ClCompile)\b[^>]*?\b(?:Include|Project)\s*=\s*""(?<spec>[^""]+)""",
		RegexOptions.CultureInvariant);

	private static readonly Regex Solution = new(@"^Project\([^)]*\)\s*=\s*""[^""]*""\s*,\s*""(?<spec>[^""]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant);

	private static readonly Regex Package = new(@"""(?:file|link):(?<spec>[^""]+)""", RegexOptions.CultureInvariant);

	private static readonly Regex CMake = new(@"\b(?:add_subdirectory|include)\s*\(\s*""?(?<spec>[^\s)""]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>Whether <paramref name="path"/> is a file the scanner reads.</summary>
	public static bool Scans(string path) => KindOf(path) is not null;

	/// <summary>The dependencies in <paramref name="text"/> of the file <paramref name="path"/>: line and specifier as written.</summary>
	public static IEnumerable<(int Line, string Specifier)> Find(string path, string text)
	{
		var regexes = KindOf(path) switch
		{
			"script" => [Script],
			"native" => [Include],
			"msbuild" => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? [Solution] : [MsBuild],
			"package" => [Package],
			"cmake" => [CMake],
			_ => Array.Empty<Regex>()
		};

		foreach (var regex in regexes)
		{
			foreach (Match match in regex.Matches(text))
			{
				var spec = match.Groups["spec"];
				var specifier = spec.Value.Trim();
				if (specifier.Length == 0 || specifier.Contains("$(") || specifier.Contains("${") || specifier.Contains("://"))
				{
					continue;
				}

				// CMake's include() of a module by name (include(CTest)) is no path.
				if (regex == CMake && !specifier.Contains('/') && !specifier.Contains('.') && match.Value.TrimStart().StartsWith("include", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				yield return (LineOf(text, spec.Index), specifier);
			}
		}
	}

	/// <summary>Whether the specifier was written by a script import (whose extension may be implied).</summary>
	public static bool IsScript(string path) => KindOf(path) == "script";

	private static string? KindOf(string path)
	{
		var name = path[(path.LastIndexOf('/') + 1)..];
		var extension = Path.GetExtension(name).ToLowerInvariant();
		return name == "package.json" ? "package"
			: name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || extension == ".cmake" ? "cmake"
			: ScriptExtensions.Contains(extension) ? "script"
			: NativeExtensions.Contains(extension) ? "native"
			: MsBuildExtensions.Contains(extension) ? "msbuild"
			: null;
	}

	private static int LineOf(string text, int index)
	{
		var line = 1;
		for (var i = 0; i < index; i++)
		{
			if (text[i] == '\n')
			{
				line++;
			}
		}

		return line;
	}
}

/// <summary>
/// Checks a component's code against its boundaries: each dependency the <see cref="DependencyScanner"/> finds is resolved
/// in the component's namespace - where each reference is nested at its path, and the references of a referenced
/// component inside it - and must stay inside the component, reach a direct reference only through its exports, and
/// never reach through a reference into the components that one nests.
/// </summary>
internal static class CodeBoundaries
{
	/// <summary>Scans <paramref name="component"/> at <paramref name="commit"/> in <paramref name="store"/>.</summary>
	public static async Task<CodeScan> ScanAsync(ComponentRegistry registry, IComponentStore store, string component, string commit)
	{
		var files = new List<(string Path, string Text)>();
		foreach (var file in await store.ListFilesAsync(commit))
		{
			if (file.Size <= DependencyScanner.MaxFileBytes && DependencyScanner.Scans(file.Path) && await store.ReadFileAsync(commit, file.Path) is { } content)
			{
				files.Add((file.Path, Encoding.UTF8.GetString(content)));
			}
		}

		return Check(registry, component, files) with { Commit = commit };
	}

	/// <summary>Checks <paramref name="files"/> (paths relative to the component's root, '/' separated) of <paramref name="component"/>.</summary>
	public static CodeScan Check(ComponentRegistry registry, string component, IEnumerable<(string Path, string Text)> files)
	{
		var owner = registry.Require(component);
		var violations = new List<CodeViolation>();
		var used = new HashSet<string>(StringComparer.Ordinal);
		var scanned = 0;
		var dependencies = 0;
		foreach (var (path, text) in files)
		{
			scanned++;
			foreach (var (line, specifier) in DependencyScanner.Find(path, text))
			{
				dependencies++;
				if (Resolve(path, specifier) is not { } resolved)
				{
					continue;
				}

				if (resolved.StartsWith("../", StringComparison.Ordinal) || resolved == "..")
				{
					var above = resolved.Split('/').SkipWhile(segment => segment == "..").FirstOrDefault();
					var target = above is not null && registry.Contains(above) ? above : null;
					violations.Add(new CodeViolation(owner.Name, path, line, specifier, CodeViolationKind.Outside, target,
						$"{owner.Name}/{path}:{line}: '{specifier}' leaves the component" +
						(target is null ? "." : owner.References.FirstOrDefault(reference => reference.Name == target) is { } nested
							? $" for '{target}'; use its nested copy at '{nested.Path}/' instead."
							: $" for '{target}', which {owner.Name} does not reference.")));
					continue;
				}

				var chain = new List<(ComponentDefinition Component, string Inside)>();
				var current = owner;
				var rest = resolved;
				while (current.References.Where(reference => rest == reference.Path || rest.StartsWith(reference.Path + "/", StringComparison.Ordinal))
					.OrderByDescending(reference => reference.Path.Length).FirstOrDefault() is { } reference
					&& registry.Find(reference.Name) is { } next)
				{
					rest = rest.Length == reference.Path.Length ? "" : rest[(reference.Path.Length + 1)..];
					chain.Add((next, rest));
					current = next;
				}

				if (chain.Count == 0)
				{
					continue;
				}

				used.Add(chain[0].Component.Name);
				if (chain.Count > 1)
				{
					var target = chain[^1].Component.Name;
					var via = string.Join(" -> ", chain.Take(chain.Count - 1).Select(step => step.Component.Name));
					violations.Add(new CodeViolation(owner.Name, path, line, specifier, CodeViolationKind.Indirect, target,
						$"{owner.Name}/{path}:{line}: '{specifier}' reaches '{target}' through {via}" +
						(owner.References.FirstOrDefault(reference => reference.Name == target) is { } direct
							? $"; use {owner.Name}'s own reference at '{direct.Path}/' instead."
							: $"; reference '{target}' from {owner.Name} itself.")));
					continue;
				}

				var (referenced, inside) = chain[0];
				if (referenced.Exports is { } exports && inside.Length > 0 && !Exported(new PathPatterns(exports), inside, DependencyScanner.IsScript(path)))
				{
					violations.Add(new CodeViolation(owner.Name, path, line, specifier, CodeViolationKind.Private, referenced.Name,
						$"{owner.Name}/{path}:{line}: '{specifier}' uses '{inside}' of {referenced.Name}, which exports only {string.Join(", ", exports)}."));
				}
			}
		}

		var unused = owner.References.Select(reference => reference.Name).Where(name => !used.Contains(name)).ToList();
		return new CodeScan(owner.Name, null, scanned, dependencies, violations, used.Order(StringComparer.Ordinal).ToList(), unused);
	}

	/// <summary>
	/// <paramref name="specifier"/> written in <paramref name="file"/>, as a path relative to the component's root ('/'
	/// separated, starting with <c>../</c> when it leaves the root), or null for an absolute path.
	/// </summary>
	public static string? Resolve(string file, string specifier)
	{
		var spec = specifier.Replace('\\', '/');
		if (spec.StartsWith('/') || (spec.Length > 1 && spec[1] == ':'))
		{
			return null;
		}

		// A wildcard item (src/**/*.cs) depends on the folder before its first wildcard.
		var wildcard = spec.IndexOfAny(['*', '?']);
		if (wildcard >= 0)
		{
			spec = spec[..wildcard];
			spec = spec.Contains('/') ? spec[..spec.LastIndexOf('/')] : "";
		}

		var segments = new List<string>(file.Split('/')[..^1]);
		var above = 0;
		foreach (var segment in spec.Split('/'))
		{
			switch (segment)
			{
				case "" or ".":
					break;
				case "..":
					if (segments.Count > 0)
					{
						segments.RemoveAt(segments.Count - 1);
					}
					else
					{
						above++;
					}

					break;
				default:
					segments.Add(segment);
					break;
			}
		}

		return string.Join("/", Enumerable.Repeat("..", above).Concat(segments));
	}

	private static bool Exported(PathPatterns exports, string path, bool script) =>
		exports.Matches(path) || (script && DependencyScanner.ImpliedScriptExtensions.Any(extension => exports.Matches(path + extension)));
}
