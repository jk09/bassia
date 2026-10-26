namespace Bassia.Core;

using Bassia.Split;

/// <summary>A config spec that does not parse or does not fit the monorepo, a view that cannot be made or changed.</summary>
internal sealed class ViewException(string message) : MonorepoException(message);

/// <summary><c>select &lt;component-pattern&gt; &lt;version&gt;</c>: the version of the mapped components the pattern matches.</summary>
internal sealed record SelectRule(int Line, TagPattern Components, string Version);

/// <summary>
/// <c>load [!]&lt;component-pattern&gt;[/&lt;path-pattern&gt;]</c>: the files of the matching components to materialize (or,
/// with <c>!</c>, not to); no path pattern is the whole component.
/// </summary>
internal sealed record LoadRule(int Line, TagPattern Components, string Pattern, bool Exclude)
{
	/// <summary>The rule as a path pattern of one component: what <see cref="PathPatterns"/> reads.</summary>
	public string ComponentPattern => Exclude ? $"!{Pattern}" : Pattern;
}

/// <summary>
/// A view's config spec, in the spirit of a ClearCase config spec: line rules read top to bottom, <c>#</c> starting a
/// comment.
/// <list type="bullet">
/// <item><c>map &lt;component&gt;[,&lt;component&gt;...]</c> maps the components and, transitively, every component they
/// reference.</item>
/// <item><c>select &lt;component-pattern&gt; &lt;version&gt;</c> picks a mapped component's version: a branch (followed by
/// <c>view update</c>), a tag or a commit hash. The first rule whose pattern (<c>*</c>, <c>?</c>) matches wins; a component
/// no rule matches is at the tip of its default branch.</item>
/// <item><c>load [!]&lt;component-pattern&gt;[/&lt;path-pattern&gt;]</c> names the files to materialize, with the path
/// patterns of split plans; <c>!</c> leaves files out. A mapped component no rule loads is mapped without any file.</item>
/// </list>
/// </summary>
internal sealed class ConfigSpec
{
	private ConfigSpec(string text, IReadOnlyList<(int Line, string Component)> maps, IReadOnlyList<SelectRule> selects, IReadOnlyList<LoadRule> loads)
	{
		Text = text;
		Maps = maps;
		Selects = selects;
		Loads = loads;
	}

	public string Text { get; }

	public IReadOnlyList<(int Line, string Component)> Maps { get; }

	public IReadOnlyList<SelectRule> Selects { get; }

	public IReadOnlyList<LoadRule> Loads { get; }

	public static ConfigSpec Parse(string text)
	{
		var maps = new List<(int Line, string Component)>();
		var selects = new List<SelectRule>();
		var loads = new List<LoadRule>();
		var lines = text.Replace("\r", "").Split('\n');
		for (var index = 0; index < lines.Length; index++)
		{
			var number = index + 1;
			var line = lines[index];
			var comment = line.IndexOf('#');
			var words = (comment < 0 ? line : line[..comment]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
			if (words.Length == 0)
			{
				continue;
			}

			var where = $"config spec line {number}";
			switch (words[0].ToLowerInvariant())
			{
				case "map":
					var names = words.Skip(1).SelectMany(word => word.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
					if (names.Count == 0)
					{
						throw new ViewException($"{where}: 'map' names no component; expected 'map <component>[,<component>...]'.");
					}

					maps.AddRange(names.Select(name => (number, name)));
					break;

				case "select":
					if (words.Length != 3)
					{
						throw new ViewException($"{where}: expected 'select <component-pattern> <version>' (a branch, a tag or a commit hash).");
					}

					selects.Add(new SelectRule(number, Pattern(words[1], where), words[2]));
					break;

				case "load":
					if (words.Length != 2)
					{
						throw new ViewException($"{where}: expected 'load [!]<component>[/<path-pattern>]' (no spaces in the pattern).");
					}

					loads.Add(ParseLoad(words[1], where, number));
					break;

				default:
					throw new ViewException($"{where}: unknown rule '{words[0]}'; expected map, select or load.");
			}
		}

		if (maps.Count == 0)
		{
			throw new ViewException("The config spec maps no component; add a rule 'map <component>'.");
		}

		return new ConfigSpec(text, maps, selects, loads);
	}

	/// <summary>
	/// One load rule, also as <c>-load</c> and <c>view load</c> take them: <c>[!]&lt;component-pattern&gt;[/&lt;path-pattern&gt;]</c>.
	/// </summary>
	public static LoadRule ParseLoad(string text, string where, int line = 0)
	{
		var exclude = text.StartsWith('!');
		var body = (exclude ? text[1..] : text).Replace('\\', '/').Trim();
		var slash = body.IndexOf('/');
		var component = slash < 0 ? body : body[..slash];
		var pattern = slash < 0 ? "" : body[(slash + 1)..].Trim('/');
		if (component.Length == 0)
		{
			throw new ViewException($"{where}: '{text}' names no component; expected [!]<component>[/<path-pattern>].");
		}

		pattern = pattern.Length == 0 ? "**" : pattern;
		try
		{
			_ = new PathPatterns([pattern]);
		}
		catch (ArgumentException ex)
		{
			throw new ViewException($"{where}: {ex.Message}");
		}

		return new LoadRule(line, Pattern(component, where), pattern, exclude);
	}

	private static TagPattern Pattern(string text, string where)
	{
		try
		{
			return TagPattern.Parse(text, where);
		}
		catch (MonorepoException ex)
		{
			throw new ViewException(ex.Message.Replace("tag pattern", "component pattern"));
		}
	}

	/// <summary>The version the first matching select rule gives <paramref name="component"/>, or null (its default branch).</summary>
	public SelectRule? SelectFor(string component) => Selects.FirstOrDefault(rule => rule.Components.Matches(component));

	/// <summary>The path patterns the load rules give <paramref name="component"/>, in order; empty when none loads it.</summary>
	public IReadOnlyList<string> LoadFor(string component) =>
		Loads.Where(rule => rule.Components.Matches(component)).Select(rule => rule.ComponentPattern).ToList();
}
