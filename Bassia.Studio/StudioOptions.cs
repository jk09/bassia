namespace Bassia.Studio;

/// <summary>
/// How Bassia Studio runs: the <c>bassia-server</c> it manages, the token for it, where it listens, and where it caches
/// objects it has read. Every switch can also come from a <c>BASSIA_STUDIO_*</c> environment variable (containers).
/// </summary>
public sealed class StudioOptions
{
	/// <summary>The base URL of the <c>bassia-server</c> (e.g. http://localhost:8080).</summary>
	public required string Server { get; init; }

	/// <summary>The server's bearer token, if it requires one.</summary>
	public string? Token { get; init; }

	/// <summary>Where Studio listens.</summary>
	public string Url { get; init; } = "http://127.0.0.1:5280";

	/// <summary>A folder for the object cache (objects are immutable, so the cache never goes stale).</summary>
	public string Cache { get; init; } = Path.Combine(Path.GetTempPath(), "bassia-studio-cache");

	/// <summary>The label shown in the title bar.</summary>
	public string? Label { get; init; }

	public static StudioOptions Parse(IReadOnlyList<string> args)
	{
		string? Get(string name)
		{
			for (var i = 0; i < args.Count - 1; i++)
			{
				if (args[i] == "-" + name)
				{
					return args[i + 1];
				}
			}

			var variable = Environment.GetEnvironmentVariable("BASSIA_STUDIO_" + name.Replace('-', '_').ToUpperInvariant());
			return string.IsNullOrEmpty(variable) ? null : variable;
		}

		var server = Get("server") ?? throw new ArgumentException("-server <bassia-server url> (or BASSIA_STUDIO_SERVER) is required.");
		var defaults = new StudioOptions { Server = server };
		return new StudioOptions
		{
			Server = server,
			Token = Get("token"),
			Url = Get("url") ?? defaults.Url,
			Cache = Get("cache") ?? defaults.Cache,
			Label = Get("label"),
		};
	}
}
