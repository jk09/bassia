namespace Bassia;

using Bassia.Cli;
using Bassia.CliCommands.Agent;
using Bassia.Core;
using Bassia.Git;

/// <summary>
/// The current user - who a bassia process acts for - and <c>bassia user ...</c> and <c>bassia whoami</c>: the monorepo's
/// user registry (<c>users.toml</c> in the meta-repo).
/// </summary>
internal static class UserCommands
{
	/// <summary>The environment variable naming the current user; it wins over every other source.</summary>
	public const string Variable = LocalIdentity.Variable;

	/// <summary>Commands that change the monorepo without a known user: becoming known, setting a monorepo up, the guide, feedback.</summary>
	private static readonly IReadOnlySet<string> Unattributed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"user add", "user use", "init", "tutorial", "feedback send", "feedback reports", "feedback pending",
		"clone", "fetch", "view create", "view load", "view update", "view remove"
	};

	/// <summary>
	/// The current user of <paramref name="monorepo"/> and where it came from, as its backend's identity knows it (see
	/// <see cref="LocalIdentity"/>). Null when none applies; an id that is not registered is an error.
	/// </summary>
	public static Task<(UserDefinition? User, string Source)> ResolveAsync(Monorepo monorepo) =>
		monorepo.Backend.Identity.CurrentAsync(monorepo.Registry.Users);

	/// <summary>
	/// Who <paramref name="spec"/> acts for: the current user, when the monorepo has users. A command that changes the
	/// monorepo is refused when the user is unknown; a read-only one, or one that needs no user (<see cref="Unattributed"/>),
	/// simply runs unattributed. The caller sets it (<see cref="Actor.Use"/>) itself: an async local set in here would not
	/// outlive this method.
	/// </summary>
	public static async Task<Actor?> ActorForAsync(CommandSpec spec)
	{
		if (Monorepo.FindRoot(Environment.CurrentDirectory) is not { } root)
		{
			return null;
		}

		var monorepo = Monorepo.Load(root);
		if (monorepo.Registry.Users.IsEmpty)
		{
			return null;
		}

		var optional = spec.ReadOnly || Unattributed.Contains(spec.FullName);
		UserDefinition? user;
		try
		{
			(user, _) = await ResolveAsync(monorepo);
		}
		catch (MonorepoException) when (optional)
		{
			user = null;
		}

		if (user is null && !optional)
		{
			throw new MonorepoException("Bassia does not know who you are in this monorepo, and every change is attributed to a user. " +
				$"Run 'bassia user use -id <id>' (registered: {string.Join(", ", monorepo.Registry.Users.Users.Select(known => known.Id))}), " +
				$"set {Variable}, or register yourself with 'bassia user add'.");
		}

		return user is null ? null : new Actor(user);
	}

	// ----- bassia whoami -----

	public static async Task<int> WhoAmIAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var (user, source) = await ResolveAsync(monorepo);
		return ProgramCli.WriteResult(true, invocation.Command,
			user is null
				? monorepo.Registry.Users.IsEmpty
					? "This monorepo has no users yet, so nothing is attributed; register yourself with 'bassia user add'."
					: "Bassia does not know who you are here; run 'bassia user use -id <id>' or set BASSIA_USER."
				: $"You are {user.Id}: {user.Display} (from {source}).",
			new Dictionary<string, object?>
			{
				["source"] = source,
				["user"] = user is null ? null : Describe(user)
			});
	}

	// ----- bassia user list | show -----

	public static Task<int> ListAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var users = monorepo.Registry.Users.Users;
		return Task.FromResult(ProgramCli.WriteResult(true, invocation.Command, $"{users.Count} user(s).", new Dictionary<string, object?>
		{
			["table"] = users.Count == 0 ? null : new TomlText(AsciiTable.Render(["ID", "NAME", "EMAIL", "CONTACT"],
				users.Select(user => (IReadOnlyList<string>)[user.Id, user.Name, user.Email, user.Contact ?? "-"]), 72)),
			["user"] = users.Select(Describe).ToList()
		}));
	}

	public static async Task<int> ShowAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var user = monorepo.Registry.Users.Require(invocation.Require("id"));
		var runs = (await monorepo.RunRecords().ListLatestAsync()).Where(run => run.User == user.Id).ToList();
		return ProgramCli.WriteResult(true, invocation.Command, $"{user.Id}: {user.Display}; {runs.Count} run(s).",
			new Dictionary<string, object?>(Describe(user))
			{
				["runs"] = runs.Count,
				["live_runs"] = runs.Where(run => run.Status == "started").Select(run => run.RunId).ToList()
			});
	}

	// ----- bassia user add | set | remove | use -----

	public static async Task<int> AddAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var user = new UserDefinition(invocation.Require("id"), invocation.Require("name"), invocation.Require("email"), invocation.Get("contact"), invocation.Get("key"));

		// Someone registering themselves is the author of their own registration.
		using var self = Actor.Current is null ? Actor.Use(new Actor(user)) : null;
		var revision = await monorepo.Graph().SetUserAsync(user, replace: false);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Registered {user.Id}: {user.Display}. Whoever works as {user.Id} runs 'bassia user use -id {user.Id}' (or sets {Variable}).",
			new Dictionary<string, object?>(Describe(user)) { ["meta_repo_commit"] = revision });
	}

	public static async Task<int> SetAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var current = monorepo.Registry.Users.Require(invocation.Require("id"));
		var user = current with
		{
			Name = invocation.Get("name") ?? current.Name,
			Email = invocation.Get("email") ?? current.Email,
			Contact = invocation.Has("clear-contact") ? null : invocation.Get("contact") ?? current.Contact,
			Key = invocation.Has("clear-key") ? null : invocation.Get("key") ?? current.Key
		};
		var revision = await monorepo.Graph().SetUserAsync(user, replace: true);
		return ProgramCli.WriteResult(true, invocation.Command, $"{user.Id} is now {user.Display}" + (user.Contact is null ? "." : $" ({user.Contact})."),
			new Dictionary<string, object?>(Describe(user)) { ["meta_repo_commit"] = revision });
	}

	public static async Task<int> RemoveAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var id = invocation.Require("id");
		var revision = await monorepo.Graph().RemoveUserAsync(id);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"Removed user {id}; what was attributed to them still names them.",
			new Dictionary<string, object?> { ["id"] = id, ["meta_repo_commit"] = revision });
	}

	public static async Task<int> UseAsync(Invocation invocation)
	{
		var monorepo = AgentCommand.LoadMonorepo();
		var user = monorepo.Registry.Users.Require(invocation.Require("id"));
		await monorepo.Backend.Config.SetAsync(ConfigFile.Find("user.id"), user.Id, ConfigLayer.User);
		return ProgramCli.WriteResult(true, invocation.Command,
			$"You are {user.Id} in this monorepo from now on: {user.Display}. Your own layer (config.user.toml) records it; {Variable} overrides it.",
			new Dictionary<string, object?>(Describe(user)) { ["config"] = monorepo.Backend.Config.LocationOf(ConfigLayer.User) });
	}

	internal static Dictionary<string, object?> Describe(UserDefinition user) => new()
	{
		["id"] = user.Id,
		["name"] = user.Name,
		["email"] = user.Email,
		["contact"] = user.Contact,
		["key"] = user.Key
	};

	/// <summary>A user id as people read it: <c>Jozef Kosik &lt;jozef@example.com&gt;; slack @jozef</c>, or the bare id when it is no longer registered.</summary>
	internal static string Reach(Monorepo monorepo, string? id) =>
		id is null ? "an unknown user"
		: monorepo.Registry.Users.Find(id) is { } user ? $"{user.Id} ({user.Name}, {user.HowToReach})" : id;
}
