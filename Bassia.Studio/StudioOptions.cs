namespace Bassia.Studio;

/// <summary>
/// How Bassia Studio runs: the <c>bassia-server</c> it manages, the token for it, where it listens, and where it caches
/// objects it has read. Every switch can also come from a <c>BASSIA_STUDIO_*</c> environment variable (containers).
/// </summary>
public sealed record StudioOptions
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

	/// <summary>
	/// Who acts, until sign-in is configured: the name recorded as owner and agent of what Studio changes, and its role.
	/// </summary>
	public string User { get; init; } = "studio";

	/// <summary>The role of <see cref="User"/>: reader, contributor or admin (default: admin, a single local user).</summary>
	public StudioRole Role { get; init; } = StudioRole.Admin;

	/// <summary>Where agentic runs execute: none (runs disabled), local (processes on this machine) or aca (Azure Container Apps jobs).</summary>
	public string RunHost { get; init; } = "none";

	/// <summary>How the local run host starts bassia (a command line, e.g. "bassia" or "dotnet /opt/bassia/Bassia.dll").</summary>
	public string Bassia { get; init; } = "bassia";

	/// <summary>Where the local run host keeps each job's folder.</summary>
	public string Runs { get; init; } = Path.Combine(Path.GetTempPath(), "bassia-studio-runs");

	/// <summary>The address jobs reach Studio at, to report output (default: where Studio listens).</summary>
	public string? PublicUrl { get; init; }

	/// <summary>The address jobs reach the server at (default: <see cref="Server"/>).</summary>
	public string? JobServer { get; init; }

	/// <summary>The Azure Container Apps job that runs agentic jobs (its resource id), with -run-host aca.</summary>
	public string? AcaJob { get; init; }

	/// <summary>The worker image the job executions run (its entrypoint runs bassia agent job).</summary>
	public string? AcaImage { get; init; }

	/// <summary>
	/// Environment variables of a job execution that come from the job's secrets, as NAME=secret pairs separated by
	/// commas (default: BASSIA_JOB_TOKEN=server-token,ANTHROPIC_API_KEY=anthropic-api-key).
	/// </summary>
	public string AcaSecrets { get; init; } = "BASSIA_JOB_TOKEN=server-token,ANTHROPIC_API_KEY=anthropic-api-key";

	/// <summary>The command line of <see cref="Bassia"/>, split on spaces (quotes keep spaces in a part).</summary>
	public IReadOnlyList<string> BassiaCommand() => SplitCommand(Bassia);

	public static IReadOnlyList<string> SplitCommand(string line)
	{
		var parts = new List<string>();
		var current = new System.Text.StringBuilder();
		var quoted = false;
		foreach (var c in line)
		{
			if (c == '"')
			{
				quoted = !quoted;
			}
			else if (c == ' ' && !quoted)
			{
				if (current.Length > 0)
				{
					parts.Add(current.ToString());
					current.Clear();
				}
			}
			else
			{
				current.Append(c);
			}
		}

		if (current.Length > 0)
		{
			parts.Add(current.ToString());
		}

		return parts;
	}

	public IReadOnlyDictionary<string, string> AcaSecretEnvironment() => AcaSecrets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1]);

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
			User = Get("user") ?? defaults.User,
			Role = Get("role") is { } role
				? Enum.TryParse<StudioRole>(role, ignoreCase: true, out var parsed) ? parsed : throw new ArgumentException($"-role is reader, contributor or admin, not '{role}'.")
				: defaults.Role,
			RunHost = Get("run-host") ?? defaults.RunHost,
			Bassia = Get("bassia") ?? defaults.Bassia,
			Runs = Get("runs") ?? defaults.Runs,
			PublicUrl = Get("public-url"),
			JobServer = Get("job-server"),
			AcaJob = Get("aca-job"),
			AcaImage = Get("aca-image"),
			AcaSecrets = Get("aca-secrets") ?? defaults.AcaSecrets,
		};
	}
}
