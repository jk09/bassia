namespace Bassia.Server;

using Bassia.Core.Model;

/// <summary>
/// How a <c>bassia-server</c> runs: where its state lives (a data folder holding the SQLite database, and the blob
/// container unless S3 holds the blobs), what it listens on, its shared token, and the landing hooks.
/// </summary>
internal sealed record ServerOptions
{
	/// <summary>The data folder: <c>state.db</c>, the signing key, and <c>objects/</c> for the local-disk blob store.</summary>
	public required string Data { get; init; }

	public string Url { get; init; } = "http://127.0.0.1:8080";

	/// <summary>The URL clients reach the server at, for signed blob URLs; default: the listening URL.</summary>
	public string? PublicUrl { get; init; }

	/// <summary>A shared bearer token for the API and the dashboard; none: open (authentication is out of scope).</summary>
	public string? Token { get; init; }

	/// <summary><c>disk</c> (the local-disk stand-in for cloud blobs) or <c>s3</c>.</summary>
	public string Storage { get; init; } = "disk";

	public string? Bucket { get; init; }

	public string? Region { get; init; }

	public string BucketPrefix { get; init; } = "objects/";

	public ChunkParameters Chunks { get; init; } = ChunkParameters.Default;

	/// <summary>The semantic merge driver (weave-driver by default, when installed; "off" disables it).</summary>
	public string SemanticDriver { get; init; } = "weave-driver";

	/// <summary>A shell command a machine-merged tree must pass before it lands; without one semantic conflicts need a replay.</summary>
	public string? Validate { get; init; }

	/// <summary>Reads options from <c>-name value</c> switches, falling back to <c>BASSIA_SERVER_*</c> environment variables (containers).</summary>
	public static ServerOptions Parse(IReadOnlyList<string> args)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < args.Count; index++)
		{
			var name = args[index].TrimStart('-');
			if (index + 1 < args.Count && !args[index + 1].StartsWith('-'))
			{
				values[name] = args[++index];
			}
			else
			{
				values[name] = "true";
			}
		}

		string? Get(string name) => values.TryGetValue(name, out var value) ? value
			: Environment.GetEnvironmentVariable("BASSIA_SERVER_" + name.ToUpperInvariant().Replace('-', '_')) is { Length: > 0 } fromEnvironment ? fromEnvironment : null;

		return new ServerOptions
		{
			Data = Path.GetFullPath(Get("data") ?? throw new ArgumentException("-data <folder> is required (or BASSIA_SERVER_DATA).")),
			Url = Get("url") ?? "http://127.0.0.1:8080",
			PublicUrl = Get("public-url"),
			Token = Get("token"),
			Storage = Get("storage") ?? "disk",
			Bucket = Get("bucket"),
			Region = Get("region"),
			BucketPrefix = Get("bucket-prefix") ?? "objects/",
			SemanticDriver = Get("semantic-driver") ?? "weave-driver",
			Validate = Get("validate")
		};
	}
}
