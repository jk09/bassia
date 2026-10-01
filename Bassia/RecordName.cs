namespace Bassia;

using System.Security.Cryptography;
using System.Text.RegularExpressions;

/// <summary>
/// The <c>&lt;key&gt;</c> of a run or integration id: <c>&lt;adjective&gt;-&lt;noun&gt;-&lt;slug&gt;</c>, e.g.
/// <c>magical-otter-vt9j3p</c>. Readable enough to show and type in full, and random enough (two words and six
/// base-36 characters) that ids never collide across workspaces sharing a component repo, without any coordination.
/// </summary>
internal static partial class RecordName
{
	public const int SlugLength = 6;
	private const string SlugAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

	private static readonly string[] Adjectives =
	[
		"agile", "amber", "ancient", "bold", "brave", "bright", "brisk", "calm", "candid", "cheerful",
		"clever", "cosmic", "crisp", "curious", "daring", "dazzling", "eager", "earnest", "elegant", "epic",
		"fearless", "festive", "fluffy", "frosty", "gentle", "gifted", "golden", "graceful", "happy", "hardy",
		"hidden", "humble", "icy", "jolly", "jovial", "keen", "kind", "lively", "lucid", "lucky",
		"magical", "majestic", "mellow", "merry", "mighty", "misty", "modest", "nimble", "noble", "patient",
		"peaceful", "plucky", "polished", "proud", "quick", "quiet", "radiant", "rapid", "rustic", "serene",
		"shiny", "silent", "silver", "sleek", "smooth", "snowy", "solar", "sparkling", "spirited", "steady",
		"stellar", "stoic", "sturdy", "sunny", "swift", "tender", "tidy", "tranquil", "trusty", "upbeat",
		"valiant", "velvet", "vibrant", "vivid", "warm", "wild", "wise", "witty", "zealous", "zesty"
	];

	private static readonly string[] Nouns =
	[
		"anchor", "apple", "aurora", "badger", "beacon", "birch", "breeze", "brook", "canyon", "cedar",
		"comet", "coral", "crane", "creek", "dawn", "delta", "dolphin", "dune", "eagle", "ember",
		"falcon", "fern", "fjord", "forest", "fox", "galaxy", "garden", "glacier", "harbor", "hawk",
		"heron", "horizon", "island", "jaguar", "kestrel", "lagoon", "lantern", "lark", "lotus", "lynx",
		"maple", "meadow", "meteor", "moon", "nebula", "oak", "ocean", "orchid", "otter", "owl",
		"panda", "pebble", "pine", "planet", "prairie", "quartz", "rabbit", "raven", "reef", "river",
		"robin", "sapphire", "sequoia", "shore", "sparrow", "spruce", "star", "stone", "summit", "sun",
		"swan", "thicket", "thunder", "tiger", "tulip", "valley", "violet", "voyage", "walrus", "willow",
		"wind", "wolf", "wren", "yak", "zebra", "zephyr", "acorn", "cloud", "glade", "puffin"
	];

	/// <summary>A fresh <c>&lt;adjective&gt;-&lt;noun&gt;-&lt;slug&gt;</c> from a cryptographic RNG.</summary>
	public static string NewKey()
	{
		var slug = string.Create(SlugLength, 0, static (chars, _) =>
		{
			for (var index = 0; index < chars.Length; index++)
			{
				chars[index] = SlugAlphabet[RandomNumberGenerator.GetInt32(SlugAlphabet.Length)];
			}
		});

		return $"{Pick(Adjectives)}-{Pick(Nouns)}-{slug}";
	}

	/// <summary>
	/// Whether <paramref name="key"/> has the shape of a key. Guards the places that turn a user-supplied name
	/// into a ref, so nothing but a well-formed key ever reaches git.
	/// </summary>
	public static bool IsKey(string key) => KeyPattern().IsMatch(key);

	private static string Pick(string[] words) => words[RandomNumberGenerator.GetInt32(words.Length)];

	[GeneratedRegex("^[a-z]+-[a-z]+-[a-z0-9]{6}$")]
	private static partial Regex KeyPattern();
}
