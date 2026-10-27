namespace Bassia.Server;

using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// The server's configuration file (<c>bassia-server -config &lt;file&gt;</c>), TOML:
/// <code>
/// url = "http://0.0.0.0:8080"          # where to listen
/// public_url = "https://bassia.example.com"
/// organization = "Example"
/// admins = ["alice"]                   # registered users holding every permission
/// default_permission = "none"          # what every authenticated user holds on every component: none or read
/// request_log = true                   # one JSON line per request on standard output
/// state = "/var/lib/bassia"            # local scratch: caches, meta-repo views
///
/// [database]
/// provider = "sqlite"                  # or "postgres"
/// connection = "Data Source=/var/lib/bassia/server.db"
///
/// [storage]
/// kind = "git"                         # a monorepo folder on this machine (one server instance)
/// path = "/srv/monorepo"
/// # kind = "cloud": bucket, table, region, prefix, s3_url, dynamodb_url (bassia-ce722c6c)
///
/// [auth]
/// signing_key = "..."                  # at least 32 characters; or BASSIA_SERVER_SIGNING_KEY
/// token_minutes = 15
/// refresh_days = 30
///
/// [[auth.oidc]]
/// name = "okta"
/// issuer = "https://example.okta.com"
/// audience = "bassia"                  # the access token's audience (default: client_id)
/// client_id = "..."                    # the public client 'bassia login' uses (authorization code + PKCE)
/// scopes = "openid email profile offline_access"
///
/// [auth.github]
/// client_id = "..."                    # an OAuth app with the device flow enabled
/// api_url = "https://api.github.com"
/// device_url = "https://github.com"
/// </code>
/// </summary>
internal sealed record ServerConfig
{
	public string Url { get; init; } = "http://127.0.0.1:8080";

	public string? PublicUrl { get; init; }

	public string? Organization { get; init; }

	public IReadOnlyList<string> Admins { get; init; } = [];

	public string DefaultPermission { get; init; } = "none";

	/// <summary>Whether every request is logged, one JSON line on standard output.</summary>
	public bool RequestLog { get; init; } = true;

	public required string State { get; init; }

	public string DatabaseProvider { get; init; } = "sqlite";

	public required string DatabaseConnection { get; init; }

	public string StorageKind { get; init; } = "git";

	public string? StoragePath { get; init; }

	public string? Bucket { get; init; }

	public string? Table { get; init; }

	public string? Region { get; init; }

	public string Prefix { get; init; } = "";

	public string? S3Url { get; init; }

	public string? DynamoUrl { get; init; }

	public required string SigningKey { get; init; }

	public int TokenMinutes { get; init; } = 15;

	public int RefreshDays { get; init; } = 30;

	public IReadOnlyList<OidcProvider> Oidc { get; init; } = [];

	public GitHubProvider? GitHub { get; init; }

	/// <summary>The server's own token issuer: what its tokens carry as <c>iss</c>.</summary>
	public const string Issuer = "bassia-server";

	public static ServerConfig Load(string path)
	{
		var text = File.ReadAllText(path);
		var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
		return Parse(text, directory);
	}

	/// <summary>Reads the configuration; relative paths are relative to <paramref name="directory"/>.</summary>
	public static ServerConfig Parse(string text, string directory)
	{
		TomlTable table;
		try
		{
			table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
		}
		catch (TomlException ex)
		{
			throw new ServerConfigException($"The server configuration is not valid TOML: {ex.Message}");
		}

		var database = Section(table, "database");
		var storage = Section(table, "storage");
		var auth = Section(table, "auth");
		var state = PathOf(Text(table, "state") ?? "state", directory);
		var signingKey = Environment.GetEnvironmentVariable("BASSIA_SERVER_SIGNING_KEY") is { Length: > 0 } fromEnvironment ? fromEnvironment : Text(auth, "signing_key");
		if (signingKey is null || signingKey.Length < 32)
		{
			throw new ServerConfigException("[auth] signing_key (or BASSIA_SERVER_SIGNING_KEY) must be set, at least 32 characters.");
		}

		var provider = Text(database, "provider") ?? "sqlite";
		if (provider is not ("sqlite" or "postgres"))
		{
			throw new ServerConfigException($"[database] provider must be sqlite or postgres, not '{provider}'.");
		}

		var kind = Text(storage, "kind") ?? "git";
		if (kind is not ("git" or "cloud"))
		{
			throw new ServerConfigException($"[storage] kind must be git or cloud, not '{kind}'.");
		}

		var defaultPermission = Text(table, "default_permission") ?? "none";
		if (defaultPermission is not ("none" or "read"))
		{
			throw new ServerConfigException($"default_permission must be none or read, not '{defaultPermission}'.");
		}

		var config = new ServerConfig
		{
			Url = Text(table, "url") ?? "http://127.0.0.1:8080",
			PublicUrl = Text(table, "public_url"),
			Organization = Text(table, "organization"),
			Admins = List(table, "admins"),
			DefaultPermission = defaultPermission,
			RequestLog = !table.TryGetValue("request_log", out var requestLog) || requestLog is not false,
			State = state,
			DatabaseProvider = provider,
			DatabaseConnection = Text(database, "connection") ?? $"Data Source={Path.Combine(state, "server.db")}",
			StorageKind = kind,
			StoragePath = Text(storage, "path") is { } storagePath ? PathOf(storagePath, directory) : null,
			Bucket = Text(storage, "bucket"),
			Table = Text(storage, "table"),
			Region = Text(storage, "region"),
			Prefix = Text(storage, "prefix") ?? "",
			S3Url = Text(storage, "s3_url"),
			DynamoUrl = Text(storage, "dynamodb_url"),
			SigningKey = signingKey,
			TokenMinutes = (int)(Number(auth, "token_minutes") ?? 15),
			RefreshDays = (int)(Number(auth, "refresh_days") ?? 30),
			Oidc = (auth.TryGetValue("oidc", out var oidc) ? oidc as TomlTableArray ?? throw new ServerConfigException("[[auth.oidc]] must be an array of tables.") : [])
				.Select(item => new OidcProvider(
					Text(item, "name") ?? Text(item, "issuer") ?? throw new ServerConfigException("[[auth.oidc]] needs an issuer."),
					Text(item, "issuer") ?? throw new ServerConfigException("[[auth.oidc]] needs an issuer."),
					Text(item, "audience") ?? Text(item, "client_id") ?? throw new ServerConfigException("[[auth.oidc]] needs an audience or a client_id."),
					Text(item, "client_id"),
					Text(item, "scopes") ?? "openid email profile offline_access"))
				.ToList(),
			GitHub = auth.TryGetValue("github", out var github) && github is TomlTable githubTable
				? new GitHubProvider(
					Text(githubTable, "client_id") ?? throw new ServerConfigException("[auth.github] needs a client_id."),
					(Text(githubTable, "api_url") ?? "https://api.github.com").TrimEnd('/'),
					(Text(githubTable, "device_url") ?? "https://github.com").TrimEnd('/'))
				: null
		};

		if (kind == "git" && config.StoragePath is null)
		{
			throw new ServerConfigException("[storage] kind = \"git\" needs a path: the monorepo folder the server serves.");
		}

		if (kind == "cloud" && (config.Bucket is null || config.Table is null))
		{
			throw new ServerConfigException("[storage] kind = \"cloud\" needs a bucket and a table.");
		}

		return config;
	}

	private static TomlTable Section(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? value as TomlTable ?? throw new ServerConfigException($"[{key}] must be a table.") : new TomlTable();

	private static string? Text(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? value as string ?? throw new ServerConfigException($"'{key}' must be a string.") : null;

	private static long? Number(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? value as long? ?? throw new ServerConfigException($"'{key}' must be a number.") : null;

	private static IReadOnlyList<string> List(TomlTable table, string key) =>
		table.TryGetValue(key, out var value)
			? (value as TomlArray ?? throw new ServerConfigException($"'{key}' must be an array of strings.")).Select(item => item as string ?? throw new ServerConfigException($"'{key}' must be an array of strings.")).ToList()
			: [];

	private static string PathOf(string path, string directory) => Path.GetFullPath(path, directory);
}

/// <summary>An OpenID Connect issuer whose tokens the server accepts, and the public client <c>bassia login</c> uses with it.</summary>
internal sealed record OidcProvider(string Name, string Issuer, string Audience, string? ClientId, string Scopes);

/// <summary>The GitHub OAuth app <c>bassia login</c> uses (device flow), and where GitHub's API is.</summary>
internal sealed record GitHubProvider(string ClientId, string ApiUrl, string DeviceUrl);

internal sealed class ServerConfigException(string message) : Exception(message);
