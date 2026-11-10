namespace Bassia.LoadTest;

using System.Globalization;

/// <summary>
/// What a run does: the state it seeds (a monorepo, its history, the workspaces that history left behind and the
/// workspaces that are open), and the load it then applies (agents working at once, in rounds). Everything follows
/// from these numbers and <see cref="Seed"/>.
/// </summary>
internal sealed record Scenario
{
	public string Name { get; init; } = "small";

	public int Seed { get; init; } = 1;

	/// <summary>Files in the seeded monorepo.</summary>
	public int Files { get; init; } = 5_000;

	/// <summary>Entries per directory of the seeded monorepo (files in a leaf directory, directories in one above).</summary>
	public int Fanout { get; init; } = 50;

	/// <summary>Landings on mainline before measuring.</summary>
	public int History { get; init; } = 200;

	/// <summary>Closed workspaces left behind (landed by the history, the rest abandoned).</summary>
	public int Closed { get; init; } = 200;

	/// <summary>Open workspaces with unlanded work, idle while the agents run.</summary>
	public int Open { get; init; } = 20;

	/// <summary>Agents working at once.</summary>
	public int Agents { get; init; } = 8;

	/// <summary>Operations per agent: rounds of create, snapshot and land.</summary>
	public int Rounds { get; init; } = 2;

	/// <summary>Snapshots an agent takes in a round before it lands.</summary>
	public int Snapshots { get; init; } = 2;

	/// <summary>Files an agent changes per snapshot.</summary>
	public int Changes { get; init; } = 3;

	/// <summary>The share of agents that also change a file other agents change, so some landings need a replay.</summary>
	public double Overlap { get; init; } = 0.25;

	/// <summary>How often each read operation is measured after the agents finished.</summary>
	public int Probes { get; init; } = 3;

	/// <summary>The parameters with their switch, what they mean and how to read and write them.</summary>
	public static readonly IReadOnlyList<Parameter> Parameters =
	[
		new("seed", "the seed everything random follows from", s => s.Seed, (s, v) => s with { Seed = (int)v }, false),
		new("files", "files in the seeded monorepo", s => s.Files, (s, v) => s with { Files = (int)v }, true),
		new("fanout", "entries per directory of the seeded monorepo", s => s.Fanout, (s, v) => s with { Fanout = (int)v }, true),
		new("history", "landings on mainline before measuring", s => s.History, (s, v) => s with { History = (int)v }, true),
		new("closed", "closed workspaces left behind by that history", s => s.Closed, (s, v) => s with { Closed = (int)v }, true),
		new("open", "open, idle workspaces with unlanded work", s => s.Open, (s, v) => s with { Open = (int)v }, true),
		new("agents", "agents working at once", s => s.Agents, (s, v) => s with { Agents = (int)v }, false),
		new("rounds", "operations per agent: rounds of create, snapshot, land", s => s.Rounds, (s, v) => s with { Rounds = (int)v }, false),
		new("snapshots", "snapshots per round", s => s.Snapshots, (s, v) => s with { Snapshots = (int)v }, false),
		new("changes", "files changed per snapshot", s => s.Changes, (s, v) => s with { Changes = (int)v }, false),
		new("overlap", "share of agents (0-1) that change a path other agents change", s => s.Overlap, (s, v) => s with { Overlap = v }, false),
		new("probes", "repetitions of each read operation after the agents", s => s.Probes, (s, v) => s with { Probes = (int)v }, false),
	];

	public static IReadOnlyList<string> Presets => ["smoke", "small", "medium", "large"];

	public static Scenario Preset(string name) => name switch
	{
		"smoke" => new Scenario { Name = name, Files = 300, Fanout = 10, History = 12, Closed = 20, Open = 4, Agents = 4, Rounds = 2, Snapshots = 2, Changes = 2, Overlap = 0.5, Probes = 2 },
		"small" => new Scenario { Name = name },
		"medium" => new Scenario { Name = name, Files = 100_000, Fanout = 100, History = 2_000, Closed = 4_000, Open = 100, Agents = 50 },
		"large" => new Scenario { Name = name, Files = 1_000_000, Fanout = 100, History = 10_000, Closed = 20_000, Open = 500, Agents = 500 },
		_ => throw new ArgumentException($"No scenario '{name}'; there are {string.Join(", ", Presets)}.")
	};

	public Scenario Validated()
	{
		void Require(bool ok, string message)
		{
			if (!ok)
			{
				throw new ArgumentException(message);
			}
		}

		Require(Files >= 1, "-files must be at least 1.");
		Require(Fanout >= 2, "-fanout must be at least 2.");
		Require(History >= 0 && Closed >= 0 && Open >= 0, "-history, -closed and -open cannot be negative.");
		Require(Agents >= 1 && Rounds >= 1 && Snapshots >= 1 && Changes >= 1, "-agents, -rounds, -snapshots and -changes must be at least 1.");
		Require(Overlap is >= 0 and <= 1, "-overlap is a share between 0 and 1.");
		Require(Probes >= 1, "-probes must be at least 1.");
		return this;
	}

	// ----- the seeded tree -----

	/// <summary>The files several agents change in the same round.</summary>
	public const int HotFiles = 8;

	public static string HotPath(int index) => $"shared/hot{index}.txt";

	/// <summary>Directory levels above a file, so that no directory holds more than <see cref="Fanout"/> entries.</summary>
	public int Depth
	{
		get
		{
			var depth = 1;
			for (long capacity = (long)Fanout * Fanout; capacity < Files; capacity *= Fanout)
			{
				depth++;
			}

			return depth;
		}
	}

	/// <summary>The path of file <paramref name="index"/>: its number in base <see cref="Fanout"/>, one digit per level.</summary>
	public string PathOf(int index)
	{
		var parts = new string[Depth + 2];
		parts[0] = "src";
		parts[^1] = $"f{index % Fanout}.txt";
		var rest = index / Fanout;
		for (var level = Depth; level >= 1; level--)
		{
			parts[level] = $"d{rest % Fanout}";
			rest /= Fanout;
		}

		return string.Join('/', parts);
	}

	/// <summary>A repeatable pseudo-random number for a purpose (<paramref name="salt"/>) and up to three coordinates.</summary>
	public ulong Random(uint salt, long a, long b = 0, long c = 0)
	{
		var x = (ulong)Seed * 0x9E3779B97F4A7C15UL ^ salt;
		foreach (var value in new[] { a, b, c })
		{
			x ^= (ulong)value + 0x9E3779B97F4A7C15UL + (x << 6) + (x >> 2);
			x ^= x >> 30;
			x *= 0xBF58476D1CE4E5B9UL;
			x ^= x >> 27;
			x *= 0x94D049BB133111EBUL;
			x ^= x >> 31;
		}

		return x;
	}

	/// <summary>
	/// A file only agent <paramref name="agent"/> changes: file numbers are dealt to agents in turn, so no two agents
	/// meet on a seeded file. With fewer files than agents, a file of the agent's own.
	/// </summary>
	public string AgentPath(int agent, int round, int snapshot, int change)
	{
		var mine = Files / Agents;
		return mine == 0
			? $"agents/a{agent}/f{change}.txt"
			: PathOf((int)(Random(11, agent, round * 1000L + snapshot, change) % (ulong)mine) * Agents + agent);
	}

	/// <summary>Whether the agent also changes a hot file each round, and which: at least two agents meet on each file in use.</summary>
	public int? HotFileOf(int agent)
	{
		var meeting = (int)Math.Round(Overlap * Agents);
		return agent < meeting ? agent % Math.Clamp(meeting / 2, 1, HotFiles) : null;
	}
}

/// <summary>A numeric parameter of a scenario. <paramref name="State"/>: it sizes the seeded state, not the load applied.</summary>
internal sealed record Parameter(string Name, string Meaning, Func<Scenario, double> Get, Func<Scenario, double, Scenario> Set, bool State)
{
	public static string Format(double value) => value.ToString(value == Math.Floor(value) ? "0" : "0.###", CultureInfo.InvariantCulture);
}
