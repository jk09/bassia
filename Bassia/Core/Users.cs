namespace Bassia.Core;

/// <summary>
/// A user of the monorepo, as the user registry (<c>users.toml</c> in the meta-repo) records them: a stable
/// <paramref name="Id"/>, a display name and an email - the identity Bassia attributes work to - plus free-form
/// <paramref name="Contact"/> details (chat handle, phone) and an optional public <paramref name="Key"/> for signing
/// later. Identity is declared, as a git author's is.
/// </summary>
internal sealed record UserDefinition(string Id, string Name, string Email, string? Contact = null, string? Key = null)
{
	/// <summary><c>Jozef Kosik &lt;jozef@example.com&gt;</c>.</summary>
	public string Display => $"{Name} <{Email}>";

	/// <summary>How to reach them: their email and, when given, their contact details.</summary>
	public string HowToReach => Contact is null ? Email : $"{Email}; {Contact}";
}

/// <summary>The monorepo's users: immutable and validated - ids valid and unique, names and emails given, emails unique.</summary>
internal sealed class UserDirectory
{
	private UserDirectory(IReadOnlyList<UserDefinition> users) => Users = users;

	public static UserDirectory Empty { get; } = new([]);

	/// <summary>The registered users, in registration order.</summary>
	public IReadOnlyList<UserDefinition> Users { get; }

	public bool IsEmpty => Users.Count == 0;

	public static UserDirectory FromDefinitions(IEnumerable<UserDefinition> users) =>
		users.Aggregate(Empty, (directory, user) => directory.Set(user, adding: true));

	public UserDefinition? Find(string id) => Users.FirstOrDefault(user => user.Id == id);

	/// <summary>The user with this email, compared without case.</summary>
	public UserDefinition? FindByEmail(string email) =>
		Users.FirstOrDefault(user => string.Equals(user.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));

	public UserDefinition Require(string id) =>
		Find(id) ?? throw new MonorepoException($"'{id}' is not a registered user. Registered: {(IsEmpty ? "none" : string.Join(", ", Users.Select(user => user.Id)))}.");

	/// <summary>Registers <paramref name="user"/>, or replaces the user with its id.</summary>
	public UserDirectory Set(UserDefinition user, bool adding = false)
	{
		Validate(user);
		var existing = Find(user.Id);
		if (adding && existing is not null)
		{
			throw new MonorepoException($"User '{user.Id}' is already registered.");
		}

		if (Users.FirstOrDefault(other => other.Id != user.Id && string.Equals(other.Email, user.Email, StringComparison.OrdinalIgnoreCase)) is { } clash)
		{
			throw new MonorepoException($"User '{clash.Id}' already has the email {user.Email}.");
		}

		return new UserDirectory(existing is null ? [.. Users, user] : Users.Select(other => other.Id == user.Id ? user : other).ToList());
	}

	public UserDirectory Remove(string id)
	{
		Require(id);
		return new UserDirectory(Users.Where(user => user.Id != id).ToList());
	}

	private static void Validate(UserDefinition user)
	{
		if (string.IsNullOrWhiteSpace(user.Id) || user.Id.StartsWith('.') || user.Id.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or ',' or '@'))
		{
			throw new MonorepoException($"'{user.Id}' is not a valid user id: no whitespace, path separators, ':', ',', '@' or leading dot.");
		}

		if (string.IsNullOrWhiteSpace(user.Name) || user.Name.Contains('<') || user.Name.Contains('>') || user.Name.Contains('\n'))
		{
			throw new MonorepoException($"User '{user.Id}' needs a name without '<', '>' or line breaks.");
		}

		var email = user.Email.Trim();
		if (email.Length == 0 || email.Any(c => char.IsWhiteSpace(c) || c is '<' or '>') || email.IndexOf('@') is < 1 || email.EndsWith('@'))
		{
			throw new MonorepoException($"User '{user.Id}' needs an email such as name@example.com; got '{user.Email}'.");
		}
	}
}

/// <summary>
/// Who the code that is running acts for: a registered <paramref name="User"/> and, for the work of an agentic run, the
/// agent it is done <paramref name="Via"/>. Set once per command (<see cref="Use"/>) and read by the storage backends
/// when they write, so every commit, tag and revision is attributed without passing an identity to each call.
/// </summary>
internal sealed record Actor(UserDefinition User, string? Via = null)
{
	private static readonly AsyncLocal<Actor?> current = new();

	/// <summary>Who the current code acts for; null when the monorepo has no users (nothing is attributed).</summary>
	public static Actor? Current => current.Value;

	/// <summary>The author of what is written: always the user.</summary>
	public (string Name, string Email) Author => (User.Name, User.Email);

	/// <summary>The committer (and tagger): the user, or <c>&lt;agent&gt; via bassia</c> with the user's email for a run's work.</summary>
	public (string Name, string Email) Committer => (Via is null ? User.Name : $"{Via} via bassia", User.Email);

	/// <summary>Acts for <paramref name="actor"/> until the returned scope is disposed (in this async flow only).</summary>
	public static IDisposable Use(Actor? actor)
	{
		var previous = current.Value;
		current.Value = actor;
		return new Scope(() => current.Value = previous);
	}

	/// <summary>The same user, acting through <paramref name="agent"/>.</summary>
	public Actor Through(string? agent) => this with { Via = agent };

	/// <summary>The agent a run's command starts: its program's name (<c>claude</c> for <c>claude -p ...</c>), or null.</summary>
	public static string? AgentOf(string command)
	{
		var program = command.TrimStart().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('"', '\'');
		if (string.IsNullOrEmpty(program))
		{
			return null;
		}

		var name = Path.GetFileNameWithoutExtension(program.Replace('\\', '/').Split('/')[^1]);
		return name.Length == 0 ? null : name;
	}

	private sealed class Scope(Action restore) : IDisposable
	{
		public void Dispose() => restore();
	}
}
