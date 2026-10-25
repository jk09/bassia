namespace Bassia.Core;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A tag pattern of a boundary rule or a visibility list: <c>*</c> matches any characters, <c>?</c> one character, and
/// everything else itself. A match captures what each wildcard matched, in order, for <c>$1</c>, <c>$2</c>, ... in the
/// rule's <c>allow</c> and <c>deny</c> patterns.
/// </summary>
internal sealed class TagPattern
{
	private readonly Regex regex;

	private TagPattern(string text)
	{
		Text = text;
		Wildcards = text.Count(c => c is '*' or '?');
		var builder = new StringBuilder("^");
		foreach (var c in text)
		{
			builder.Append(c switch
			{
				'*' => "(.*)",
				'?' => "(.)",
				_ => Regex.Escape(c.ToString())
			});
		}

		regex = new Regex(builder.Append('$').ToString(), RegexOptions.CultureInvariant);
	}

	public string Text { get; }

	/// <summary>How many wildcards the pattern has: the highest <c>$n</c> a rule built on it may use.</summary>
	public int Wildcards { get; }

	public static TagPattern Parse(string text, string where)
	{
		if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace) || text.Contains(','))
		{
			throw new MonorepoException($"{where}: '{text}' is not a valid tag pattern: no whitespace or commas.");
		}

		return new TagPattern(text);
	}

	/// <summary>What the wildcards matched when <paramref name="tag"/> matches, else null.</summary>
	public IReadOnlyList<string>? Match(string tag)
	{
		var match = regex.Match(tag);
		return match.Success ? match.Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToList() : null;
	}

	public bool Matches(string tag) => regex.IsMatch(tag);

	/// <summary>The pattern with <c>$1</c>, <c>$2</c>, ... replaced by <paramref name="captures"/>.</summary>
	public static string Substitute(string text, IReadOnlyList<string> captures) =>
		Regex.Replace(text, @"\$(\d+)", match => int.Parse(match.Groups[1].Value) is var n && n >= 1 && n <= captures.Count ? captures[n - 1] : match.Value);

	/// <summary>The highest <c>$n</c> in <paramref name="text"/>, or 0.</summary>
	public static int HighestCapture(string text) =>
		Regex.Matches(text, @"\$(\d+)").Select(match => int.Parse(match.Groups[1].Value)).DefaultIfEmpty(0).Max();

	public override string ToString() => Text;
}

/// <summary>
/// A boundary rule of <c>components.toml</c> (a <c>[[boundary]]</c> table): a component with a tag matching
/// <paramref name="Source"/> may reference only components with a tag matching one of <paramref name="Allow"/> (when
/// given) and none matching <paramref name="Deny"/>. <c>$n</c> in those patterns is what the source's n-th wildcard
/// matched.
/// </summary>
internal sealed record BoundaryRule(string Source, IReadOnlyList<string> Allow, IReadOnlyList<string> Deny, string? Description = null)
{
	/// <summary>The rule as one line: <c>type:app allow type:lib,type:ui deny type:app</c>.</summary>
	public string Text =>
		Source + (Allow.Count > 0 ? $" allow {string.Join(",", Allow)}" : "") + (Deny.Count > 0 ? $" deny {string.Join(",", Deny)}" : "");

	/// <summary>Throws when the rule cannot be applied: no source, nothing allowed or denied, a bad pattern or <c>$n</c>.</summary>
	public void Validate(string where)
	{
		if (string.IsNullOrWhiteSpace(Source))
		{
			throw new MonorepoException($"{where} has no 'source'.");
		}

		if (Allow.Count == 0 && Deny.Count == 0)
		{
			throw new MonorepoException($"{where} ({Source}) needs 'allow', 'deny' or both.");
		}

		var source = TagPattern.Parse(Source, where);
		foreach (var pattern in Allow.Concat(Deny))
		{
			TagPattern.Parse(pattern, where);
			if (TagPattern.HighestCapture(pattern) is var n && n > source.Wildcards)
			{
				throw new MonorepoException($"{where}: '{pattern}' uses ${n}, but the source '{Source}' has {source.Wildcards} wildcard(s).");
			}
		}
	}
}

/// <summary>What a violation breaks: a <see cref="BoundaryRule"/>, or the referenced component's visibility.</summary>
internal enum ViolationKind
{
	Rule,
	Visibility
}

/// <summary>
/// One reference <paramref name="From"/> → <paramref name="To"/> that breaks a boundary rule or a visibility.
/// <see cref="Key"/> identifies it across registry changes (renumbering the rules does not make it new).
/// </summary>
internal sealed record BoundaryViolation(string From, string To, ViolationKind Kind, string Constraint, string Message)
{
	public string Key => $"{From}\n{To}\n{Kind}\n{Constraint}";
}

/// <summary>A registry change refused because it adds violations that were not in the graph before.</summary>
internal sealed class BoundaryViolationException(IReadOnlyList<BoundaryViolation> violations)
	: MonorepoException(violations.Count == 1
		? $"The change breaks the monorepo's boundaries: {violations[0].Message}"
		: $"The change breaks the monorepo's boundaries in {violations.Count} places: {string.Join(" ", violations.Select(violation => violation.Message))}")
{
	public IReadOnlyList<BoundaryViolation> Violations { get; } = violations;
}

/// <summary>Checks a component graph against its boundary rules and the components' visibility.</summary>
internal static class Boundaries
{
	/// <summary>The tag every component has without being given it: <c>component:&lt;name&gt;</c>.</summary>
	public const string ComponentTagPrefix = "component:";

	/// <summary>A component's tags, the implicit <c>component:&lt;name&gt;</c> first.</summary>
	public static IReadOnlyList<string> TagsOf(ComponentDefinition component) => [ComponentTagPrefix + component.Name, .. component.Tags];

	/// <summary>Throws when a tag cannot be used: empty, whitespace, a comma, a wildcard or <c>$</c>, or the implicit prefix.</summary>
	public static void ValidateTags(string owner, IReadOnlyList<string> tags)
	{
		foreach (var tag in tags)
		{
			if (string.IsNullOrWhiteSpace(tag) || tag.Any(c => char.IsWhiteSpace(c) || c is ',' or '*' or '?' or '$'))
			{
				throw new MonorepoException($"Component '{owner}': '{tag}' is not a valid tag: no whitespace, commas, '*', '?' or '$'.");
			}

			if (tag.StartsWith(ComponentTagPrefix, StringComparison.Ordinal))
			{
				throw new MonorepoException($"Component '{owner}': '{tag}' is implicit - every component has the tag {ComponentTagPrefix}<its name>.");
			}
		}

		if (tags.GroupBy(tag => tag, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
		{
			throw new MonorepoException($"Component '{owner}' has the tag '{twice.Key}' more than once.");
		}
	}

	public static void ValidateVisibility(string owner, IReadOnlyList<string>? visibility)
	{
		foreach (var pattern in visibility ?? [])
		{
			TagPattern.Parse(pattern, $"Component '{owner}' visibility");
		}
	}

	/// <summary>Every violation in <paramref name="registry"/>: references in registration order, rules in file order.</summary>
	public static IReadOnlyList<BoundaryViolation> Check(ComponentRegistry registry)
	{
		var violations = new List<BoundaryViolation>();
		var rules = registry.Boundaries.Select((rule, index) => (Rule: rule, Index: index + 1, Source: TagPattern.Parse(rule.Source, $"[[boundary]] #{index + 1}"))).ToList();
		foreach (var from in registry.Components)
		{
			var fromTags = TagsOf(from);
			foreach (var reference in from.References)
			{
				if (registry.Find(reference.Name) is not { } to)
				{
					continue;
				}

				var toTags = TagsOf(to);
				if (to.Visibility is { } visibility && !visibility.Any(pattern => fromTags.Any(TagPattern.Parse(pattern, $"Component '{to.Name}' visibility").Matches)))
				{
					violations.Add(new BoundaryViolation(from.Name, to.Name, ViolationKind.Visibility, string.Join(",", visibility),
						visibility.Count == 0
							? $"{from.Name} -> {to.Name}: {to.Name} is private (visibility = [])."
							: $"{from.Name} -> {to.Name}: {to.Name} is visible only to {string.Join(", ", visibility)}; {from.Name} has {string.Join(", ", fromTags)}."));
				}

				foreach (var (rule, index, source) in rules)
				{
					foreach (var tag in fromTags)
					{
						if (source.Match(tag) is not { } captures)
						{
							continue;
						}

						var allow = rule.Allow.Select(pattern => TagPattern.Parse(TagPattern.Substitute(pattern, captures), $"[[boundary]] #{index}")).ToList();
						var deny = rule.Deny.Select(pattern => TagPattern.Parse(TagPattern.Substitute(pattern, captures), $"[[boundary]] #{index}")).ToList();
						var applied = $"{rule.Text} (as {tag})";
						if (allow.Count > 0 && !toTags.Any(candidate => allow.Any(pattern => pattern.Matches(candidate))))
						{
							violations.Add(new BoundaryViolation(from.Name, to.Name, ViolationKind.Rule, applied,
								$"{from.Name} -> {to.Name}: boundary #{index} ({rule.Text}) lets {tag} reference only {string.Join(", ", allow)}; {to.Name} has {string.Join(", ", toTags)}."));
						}

						if (toTags.FirstOrDefault(candidate => deny.Any(pattern => pattern.Matches(candidate))) is { } denied)
						{
							violations.Add(new BoundaryViolation(from.Name, to.Name, ViolationKind.Rule, applied + " deny",
								$"{from.Name} -> {to.Name}: boundary #{index} ({rule.Text}) forbids {tag} to reference {denied}."));
						}
					}
				}
			}
		}

		return violations;
	}

	/// <summary>The violations of <paramref name="after"/> that <paramref name="before"/> did not have.</summary>
	public static IReadOnlyList<BoundaryViolation> Added(ComponentRegistry before, ComponentRegistry after)
	{
		var known = Check(before).Select(violation => violation.Key).ToHashSet(StringComparer.Ordinal);
		return Check(after).Where(violation => !known.Contains(violation.Key)).ToList();
	}
}
