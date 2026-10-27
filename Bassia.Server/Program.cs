namespace Bassia.Server;

using Bassia.Storage.Cloud;

/// <summary>
/// <c>bassia-server</c>: runs the Bassia server, or does a one-off job with its configuration.
/// <code>
/// bassia-server -config server.toml [-url http://0.0.0.0:8080]   run the server
/// bassia-server token -config server.toml -user alice [-minutes 60]  print a server token for a registered user
/// bassia-server migrate -config server.toml                       bring the database schema up to date
/// bassia-server check -config server.toml                         check the AWS resources (versioning, recovery)
/// </code>
/// </summary>
internal static class Program
{
	private const string Usage = """
		Usage:
		  bassia-server -config <file> [-url <url>]                 run the server
		  bassia-server token -config <file> -user <id> [-minutes <n>]   print a token for a registered user (bootstrap, automation)
		  bassia-server migrate -config <file>                       bring the database schema up to date
		  bassia-server check -config <file>                         check the cloud resources the configuration names
		""";

	public static async Task<int> Main(string[] args)
	{
		var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
		var options = Options(args.SkipWhile(arg => !arg.StartsWith('-')).ToArray());
		if (command is "help" or "-help" or "--help" || !options.TryGetValue("config", out var configPath))
		{
			Console.Out.WriteLine(Usage);
			return command is "help" ? 0 : 2;
		}

		try
		{
			var config = ServerConfig.Load(configPath);
			switch (command)
			{
				case "serve":
					var state = await ServerState.CreateAsync(config);
					var app = ServerApp.Build(state, options.GetValueOrDefault("url"));
					Console.Error.WriteLine($"Bassia server listening on {options.GetValueOrDefault("url") ?? config.Url} ({config.StorageKind} storage, {config.DatabaseProvider} database).");
					await app.RunAsync();
					return 0;
				case "token":
					var tokenState = await ServerState.CreateAsync(config);
					var user = options.GetValueOrDefault("user") ?? throw new ServerConfigException("token needs -user <id>.");
					if (!config.Admins.Contains(user) && (await tokenState.Meta.Store.LoadUsersAsync()).All(candidate => candidate.Id != user))
					{
						throw new ServerConfigException($"'{user}' is neither a registered user (users.toml of the meta-repo) nor one of the configured admins.");
					}

					var minutes = int.TryParse(options.GetValueOrDefault("minutes"), out var value) ? value : config.TokenMinutes;
					Console.Out.WriteLine(tokenState.Tokens.Issue(user, TimeSpan.FromMinutes(minutes)));
					return 0;
				case "migrate":
					await Data.ServerDatabase.Open(config.DatabaseProvider, config.DatabaseConnection).MigrateAsync();
					Console.Out.WriteLine("The database schema is up to date.");
					return 0;
				case "check":
					if (config.StorageKind != "cloud")
					{
						Console.Out.WriteLine("The storage is a folder on this machine; nothing to check.");
						return 0;
					}

					var problems = await new AwsCloudOptions(config.Bucket!, config.Table!, config.Region, config.Prefix, config.S3Url, config.DynamoUrl).CheckAsync();
					foreach (var problem in problems)
					{
						Console.Out.WriteLine(problem);
					}

					return problems.Count == 0 ? 0 : 1;
				default:
					Console.Error.WriteLine($"Unknown command '{command}'.\n{Usage}");
					return 2;
			}
		}
		catch (ServerConfigException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	/// <summary><c>-name value</c> pairs.</summary>
	private static Dictionary<string, string> Options(string[] args)
	{
		var options = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var i = 0; i < args.Length; i++)
		{
			if (args[i].StartsWith('-'))
			{
				options[args[i].TrimStart('-')] = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "true";
			}
		}

		return options;
	}
}
