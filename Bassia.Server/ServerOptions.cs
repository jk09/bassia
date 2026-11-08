namespace Bassia.Server;

using Bassia.Core.Model;
using Bassia.Stores;

/// <summary>
/// How a <c>bassia-server</c> runs: where its state lives (PostgreSQL for heads, the path index and leases; a data folder
/// for the signing key, and for the blobs unless S3 or Azure holds them), what it listens on, its shared token, and the
/// landing hooks.
/// </summary>
internal sealed record ServerOptions
{
	/// <summary>The data folder: the signing key, and <c>objects/</c> for the local-disk blob store.</summary>
	public required string Data { get; init; }

	public string Url { get; init; } = "http://127.0.0.1:8080";

	/// <summary>The URL clients reach the server at, for signed blob URLs; default: the listening URL.</summary>
	public string? PublicUrl { get; init; }

	/// <summary>The shared bearer token for the API; none: one the server generates and keeps in the data folder.</summary>
	public string? Token { get; init; }

	/// <summary>Serve everyone, with no token at all. Only ever on request: a server is closed by default.</summary>
	public bool NoAuth { get; init; }

	/// <summary>Browser origins (scheme, host and port) allowed to call the API across origins (CORS); none: no CORS headers at all.</summary>
	public IReadOnlyList<string> CorsOrigins { get; init; } = [];

	/// <summary><c>disk</c> (the local-disk stand-in for cloud blobs), <c>s3</c> or <c>azure</c>.</summary>
	public string Storage { get; init; } = "disk";

	public string? Bucket { get; init; }

	public string? Region { get; init; }

	public string BucketPrefix { get; init; } = "objects/";

	/// <summary>Azure Blob Storage: an account connection string (account key, or Azurite's development string).</summary>
	public string? AzureConnection { get; init; }

	/// <summary>Azure Blob Storage: a container URL, used with Microsoft Entra ID (managed identity) when there is no connection string.</summary>
	public string? AzureUrl { get; init; }

	/// <summary>The container, with a connection string (default <c>bassia</c>).</summary>
	public string AzureContainer { get; init; } = "bassia";

	/// <summary>
	/// The PostgreSQL connection string: heads, the path index and leases (Azure Database for PostgreSQL: add
	/// <c>SSL Mode=Require</c>). Required; the server has no other state backend.
	/// </summary>
	public string? Postgres { get; init; }

	/// <summary>
	/// What the server does about its PostgreSQL schema on start: <c>auto</c> (the default) migrates it under an advisory
	/// lock; <c>verify</c> changes nothing and refuses to start unless the schema is usable, for deployments that run
	/// <c>bassia-server migrate</c> as a release step first.
	/// </summary>
	public SchemaMode Migrate { get; init; } = SchemaMode.Migrate;

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

		// PostgreSQL is the server's only state backend; -state postgres stays accepted for existing deployments.
		if (Get("state") is { } state && !state.Equals("postgres", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"-state '{state}' is not supported: bassia-server keeps its state in PostgreSQL only " +
				"Pass -postgres <connection string>.");
		}

		var noAuth = Get("no-auth") is { } open && !open.Equals("false", StringComparison.OrdinalIgnoreCase);
		if (noAuth && Get("token") is not null)
		{
			throw new ArgumentException("-no-auth and -token contradict each other: pass one of them.");
		}

		return new ServerOptions
		{
			NoAuth = noAuth,
			Data = Path.GetFullPath(Get("data") ?? throw new ArgumentException("-data <folder> is required (or BASSIA_SERVER_DATA).")),
			Url = Get("url") ?? "http://127.0.0.1:8080",
			PublicUrl = Get("public-url"),
			Token = Get("token"),
			CorsOrigins = Get("cors-origin") is { } origins ? CorsList(origins) : [],
			Storage = Get("storage") ?? "disk",
			Bucket = Get("bucket"),
			Region = Get("region"),
			BucketPrefix = Get("bucket-prefix") ?? Get("azure-prefix") ?? "objects/",
			AzureConnection = Get("azure-connection"),
			AzureUrl = Get("azure-url"),
			AzureContainer = Get("azure-container") ?? "bassia",
			Postgres = Get("postgres"),
			Migrate = (Get("migrate") ?? "auto").ToLowerInvariant() switch
			{
				"auto" => SchemaMode.Migrate,
				"verify" => SchemaMode.Verify,
				var other => throw new ArgumentException($"-migrate '{other}' is not supported: use auto (the default) or verify.")
			},
			SemanticDriver = Get("semantic-driver") ?? "weave-driver",
			Validate = Get("validate")
		};
	}

	/// <summary>Comma-separated origins, each an absolute http(s) URL without a path; a wildcard is refused (the API takes a token that changes the monorepo).</summary>
	private static IReadOnlyList<string> CorsList(string text)
	{
		var origins = new List<string>();
		foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (item == "*")
			{
				throw new ArgumentException("-cors-origin '*' is not allowed: name each origin, e.g. https://studio.example.com.");
			}

			if (!Uri.TryCreate(item, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.PathAndQuery != "/")
			{
				throw new ArgumentException($"-cors-origin '{item}' is not an origin: give scheme, host and optional port, e.g. https://studio.example.com.");
			}

			origins.Add(uri.GetLeftPart(UriPartial.Authority));
		}

		return origins;
	}
}
