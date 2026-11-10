namespace Bassia.LoadTest;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Stores;
using Npgsql;

/// <summary>Where a run happens: a server the harness hosts (on <see cref="Postgres"/>, state in <see cref="Data"/>) or one that is already running.</summary>
internal sealed record RunOptions
{
	/// <summary>The PostgreSQL a hosted server uses; each run gets a schema of its own.</summary>
	public string? Postgres { get; init; }

	/// <summary>A running server to measure instead of hosting one.</summary>
	public string? ServerUrl { get; init; }

	public string? Token { get; init; }

	/// <summary>A folder that keeps the seeded state (blobs, and the name of its schema) for later runs; without it the state is dropped.</summary>
	public string? Data { get; init; }

	/// <summary>Whether a remote server is seeded first (a hosted one always is, unless its kept state is reused).</summary>
	public bool SeedRemote { get; init; } = true;

	public bool GarbageCollection { get; init; } = true;
}

/// <summary>
/// Runs one scenario: seeds the state, lets the agents work in rounds against the server over HTTP, measures the read
/// operations on the state that leaves, and reports.
/// </summary>
internal sealed class Runner(Scenario scenario, RunOptions options, Action<string> log)
{
	private const string HostedToken = "loadtest";
	private const string MarkerFile = "loadtest-state.toml";

	/// <summary>Connections each of a hosted server's two stores may hold, so a default PostgreSQL (100 connections) is enough.</summary>
	private const int PoolSize = 40;

	private readonly Recorder recorder = new();

	/// <summary>The one connection every agent and probe of the run talks through.</summary>
	private ServerConnection client = null!;

	/// <summary>Mainline's seq when the agents start: part of what they write, so a run on a kept state makes changes of its own.</summary>
	private long epoch;

	public async Task<RunReport> RunAsync(CancellationToken ct = default) =>
		options.ServerUrl is { } url ? await RunRemoteAsync(url, ct) : await RunHostedAsync(ct);

	// ----- a server the harness hosts -----

	private async Task<RunReport> RunHostedAsync(CancellationToken ct)
	{
		var postgres = options.Postgres ?? throw new ArgumentException(
			"A hosted run needs a PostgreSQL: pass -postgres <connection string> or set BASSIA_TEST_POSTGRES. To measure a running server, pass -server <url>.");
		var kept = options.Data is not null;
		var data = Path.GetFullPath(options.Data ?? Path.Combine(Path.GetTempPath(), $"bassia-loadtest-{Guid.NewGuid():N}"));
		Directory.CreateDirectory(data);
		var marker = Path.Combine(data, MarkerFile);
		var reuse = File.Exists(marker);
		var schema = reuse ? ReadMarker(marker) : "lt" + Guid.NewGuid().ToString("N")[..12];
		var connection = new NpgsqlConnectionStringBuilder(postgres) { SearchPath = schema, MaxPoolSize = PoolSize, Timeout = 300, CommandTimeout = 3600 }.ToString();
		try
		{
			SeedResult? seed = null;
			if (!reuse)
			{
				Execute(postgres, $"CREATE SCHEMA {schema}");
				seed = await SeedHostedAsync(connection, data, ct);
				// A database in use has statistics (autovacuum); one loaded a moment ago has none, and plans as if it were empty.
				Execute(connection, "ANALYZE");
				if (kept)
				{
					File.WriteAllText(marker, Marker(schema));
				}
			}
			else
			{
				log($"reusing the state in '{data}' (schema {schema})");
			}

			using var probe = new ServerProbe();
			await using var server = new ServerApp(new ServerOptions
			{
				Data = data,
				Postgres = connection,
				Url = "http://127.0.0.1:0",
				Token = HostedToken,
				SemanticDriver = "off",
				DecorateBlobs = blobs => new ProbedObjectStore(blobs, probe),
				RequestProbe = probe.InvokeAsync
			});

			// At start a node reconciles every open workspace without a current candidate.
			var started = Stopwatch.GetTimestamp();
			await server.StartAsync();
			await IdleAsync(server, ct);
			recorder.Of(Operations.ReconcileCatchUp).Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
				new Cost { Statements = probe.Background.Statements, BlobCalls = probe.Background.BlobCalls });

			return await MeasureAsync(server.Address!, HostedToken, "hosted (local-disk blobs, PostgreSQL)", !reuse, seed, server, probe, ct);
		}
		finally
		{
			if (!kept)
			{
				try
				{
					Execute(postgres, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
					Directory.Delete(data, true);
				}
				catch (Exception ex) when (ex is IOException or NpgsqlException)
				{
					// Cleaning up must not hide what went wrong in the run.
				}
			}
		}
	}

	private async Task<SeedResult> SeedHostedAsync(string connection, string data, CancellationToken ct)
	{
		// The same stores the server will open, without the server: its reconciliation would follow every seeded landing.
		await using var state = new PostgresState(connection);
		await using var trees = new PostgresTreeStore(connection);
		var repo = new Repository(new DiskObjectStore(Path.Combine(data, "objects")), state, state, state, null, trees, state);
		await repo.InitializeAsync("bassia-server");
		return await new Seeder(repo, new LandingService(repo), scenario, log).SeedAsync(ct);
	}

	private static async Task IdleAsync(ServerApp server, CancellationToken ct)
	{
		while (!server.Reconciling.Idle)
		{
			await Task.Delay(10, ct);
		}
	}

	private string Marker(string schema) =>
		$"# The state of a bassia load test; delete this folder and drop the schema to seed again.\nschema = \"{schema}\"\n" +
		string.Concat(Scenario.Parameters.Where(parameter => parameter.State || parameter.Name == "seed").Select(parameter => $"{parameter.Name} = {Parameter.Format(parameter.Get(scenario))}\n"));

	/// <summary>The schema of a kept state, after checking that it was seeded for this scenario.</summary>
	private string ReadMarker(string file)
	{
		var values = File.ReadAllLines(file).Where(line => line.Contains('=') && !line.StartsWith('#'))
			.ToDictionary(line => line.Split('=')[0].Trim(), line => line.Split('=', 2)[1].Trim().Trim('"'));
		foreach (var parameter in Scenario.Parameters.Where(parameter => parameter.State || parameter.Name == "seed"))
		{
			if (!values.TryGetValue(parameter.Name, out var stored) || stored != Parameter.Format(parameter.Get(scenario)))
			{
				throw new ArgumentException($"The state in '{Path.GetDirectoryName(file)}' was seeded with {parameter.Name} = {stored ?? "?"}, not {Parameter.Format(parameter.Get(scenario))}; " +
					"use another -data folder or the same parameters.");
			}
		}

		return values["schema"];
	}

	private static void Execute(string postgres, string sql)
	{
		using var connection = new NpgsqlConnection(postgres);
		connection.Open();
		using var command = new NpgsqlCommand(sql, connection);
		command.ExecuteNonQuery();
	}

	// ----- a server that is already running -----

	private async Task<RunReport> RunRemoteAsync(string url, CancellationToken ct)
	{
		SeedResult? seed = null;
		if (options.SeedRemote)
		{
			using var connection = new ServerConnection(url, options.Token);
			seed = await new Seeder(Client(connection, null), new RemoteLanding(connection), scenario, log).SeedAsync(ct);
		}

		return await MeasureAsync(url, options.Token, url, options.SeedRemote, seed, null, null, ct);
	}

	// ----- the measured part -----

	/// <summary>A client's core over the remote stores, as a job container has it: objects and trees cached in memory.</summary>
	private static Repository Client(ServerConnection connection, IObjectStore? sharedObjects) =>
		new(sharedObjects ?? new CachingObjectStore(new MemoryObjectStore(), new RemoteObjectStore(connection)),
			new RemoteViewStore(connection), new RemotePathIndex(connection), new RemoteLeaseStore(connection), null,
			new CachingTreeStore(new MemoryTreeStore(), new RemoteTreeStore(connection)), new RemoteWorkspaceIndex(connection));

	private async Task<RunReport> MeasureAsync(string url, string? token, string serverName, bool seeded, SeedResult? seed, ServerApp? server, ServerProbe? probe, CancellationToken ct)
	{
		using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
		using var connection = new ServerConnection(url, token, new ClientProbe(handler));
		client = connection;
		var info = await connection.GetAsync<Api.Info>("info", ct);
		var before = await Client(connection, null).GetMainAsync(ct);
		epoch = before.Seq;
		log($"{scenario.Agents} agent(s), {scenario.Rounds} round(s), on mainline seq {before.Seq}");

		// The agents share one object cache, as agents on one machine share .bassia/cache.
		var shared = new CachingObjectStore(new MemoryObjectStore(), new RemoteObjectStore(connection));
		var agents = Enumerable.Range(0, scenario.Agents).Select(index => new Agent(index, Client(connection, shared))).ToList();

		using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var snapshots = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
		var watch = await WatchAsync(connection, snapshots, watching.Token);

		var background = probe is null ? null : new Cost { Statements = probe.Background.Statements, BlobCalls = probe.Background.BlobCalls };
		var load = Stopwatch.StartNew();
		for (var round = 0; round < scenario.Rounds; round++)
		{
			await RoundAsync(agents, round, snapshots);
			log($"  round {round + 1} of {scenario.Rounds} done ({load.Elapsed.TotalSeconds:0.0} s)");
		}

		if (server is not null)
		{
			await IdleAsync(server, ct);
		}

		var loadSeconds = load.Elapsed.TotalSeconds;
		await watching.CancelAsync();
		await watch;

		var main = await Client(connection, null).GetMainAsync(ct);
		var landings = main.Seq - before.Seq;
		BackgroundResult? backgroundResult = probe is null ? null
			: new BackgroundResult(probe.Background.Statements - background!.Statements, probe.Background.BlobCalls - background.BlobCalls, landings);

		var open = await ProbeAsync(connection, server, main, ct);
		var heads = 0;
		await foreach (var _ in new RemoteViewStore(connection).ListHeadsAsync(HeadName.WorkspacePrefix, ct))
		{
			heads++;
		}

		return new RunReport(scenario, serverName, DateTimeOffset.UtcNow, info.Version, seeded, seed, main.Seq, main.Tree.Hex, open, heads - open, loadSeconds, landings,
			backgroundResult, recorder.Results());
	}

	private sealed class Agent(int index, Repository repo)
	{
		public int Index => index;

		public string Name => $"agent{index}";

		public Repository Repo => repo;

		public WorkspaceService Service { get; } = new(repo);

		public WorkspaceState Workspace { get; set; } = null!;

		public Lease? Lease { get; set; }

		/// <summary>Everything the agent changed this round, to do again in a replay.</summary>
		public List<PathChange> Changes { get; } = [];
	}

	/// <summary>
	/// One round. First every agent creates a workspace, announces itself with a lease and takes its snapshots, all at
	/// once. Then they land: agents with paths of their own at once, and the agents meeting on a hot file one after
	/// another in the order of their numbers - so who lands and who has to replay does not depend on timing.
	/// </summary>
	private async Task RoundAsync(List<Agent> agents, int round, ConcurrentDictionary<string, long> snapshots)
	{
		await Task.WhenAll(agents.Select(agent => Task.Run(async () =>
		{
			agent.Changes.Clear();
			agent.Workspace = await recorder.TimeAsync(Operations.WorkspaceCreate, () => agent.Service.CreateAsync($"a{agent.Index}-r{round}", agent.Name, intent: $"round {round}"));
			agent.Lease = (await recorder.TimeAsync(Operations.LeaseAcquire, async () =>
				await agent.Repo.Leases.AcquireAsync(new LeaseRequest($"agents/a{agent.Index}", LeaseMode.Intent, agent.Workspace.Id, TimeSpan.FromMinutes(10))))).Lease;
			for (var snapshot = 0; snapshot < scenario.Snapshots; snapshot++)
			{
				var changes = new List<(string Path, byte[] Content)>();
				for (var change = 0; change < scenario.Changes; change++)
				{
					var path = scenario.AgentPath(agent.Index, round, snapshot, change);
					changes.Add((path, Content($"seed {scenario.Seed} from {epoch} agent {agent.Index} round {round} snapshot {snapshot} {path}")));
				}

				if (snapshot == scenario.Snapshots - 1 && scenario.HotFileOf(agent.Index) is { } hot)
				{
					changes.Add((Scenario.HotPath(hot), Content($"seed {scenario.Seed} from {epoch} agent {agent.Index} round {round} hot {hot}")));
				}

				await SnapshotAsync(agent, changes, snapshots);
			}
		})));

		var alone = agents.Where(agent => scenario.HotFileOf(agent.Index) is null).Select(agent => Task.Run(async () =>
		{
			if (agent.Index % 2 == 1)
			{
				await recorder.TimeAsync(Operations.Sync, () => agent.Service.SyncAsync(agent.Workspace.Id, null, agent.Name));
			}

			await LandAsync(agent, round, snapshots);
		}));
		var meeting = agents.Where(agent => scenario.HotFileOf(agent.Index) is not null).GroupBy(agent => scenario.HotFileOf(agent.Index)).Select(group => Task.Run(async () =>
		{
			foreach (var agent in group.OrderBy(agent => agent.Index))
			{
				await LandAsync(agent, round, snapshots);
			}
		}));
		await Task.WhenAll(alone.Concat(meeting));

		await Task.WhenAll(agents.Where(agent => agent.Lease is not null).Select(agent => Task.Run(async () =>
			await recorder.TimeAsync(Operations.LeaseRelease, async () =>
			{
				await agent.Repo.Leases.ReleaseAsync(agent.Lease!.Id, agent.Lease.Token);
				return 0;
			}))));
	}

	private static byte[] Content(string text) => Encoding.UTF8.GetBytes(text.PadRight(160, '.') + "\n");

	/// <summary>A snapshot as a client takes it: the changed files' content, the rebuilt trees and the workspace's new head, in one batch.</summary>
	private async Task SnapshotAsync(Agent agent, IReadOnlyList<(string Path, byte[] Content)> files, ConcurrentDictionary<string, long> snapshots)
	{
		var repo = agent.Repo;
		await recorder.TimeAsync(Operations.Snapshot, () => repo.Objects.BatchAsync(async () =>
		{
			var changes = new List<PathChange>(files.Count);
			foreach (var (path, content) in files)
			{
				changes.Add(new PathChange(path, await repo.Objects.WriteContentAsync(content)));
			}

			agent.Changes.RemoveAll(change => changes.Any(other => other.Path == change.Path));
			agent.Changes.AddRange(changes);
			var current = await repo.FindWorkspaceAsync(agent.Workspace.Id) ?? throw new VersionException($"No workspace '{agent.Workspace.Id}'.");
			var working = await repo.Objects.ReadCommitAsync(current.Record.Working);
			var tree = await repo.Trees.EditAsync(working.Tree, changes);
			return await agent.Service.SetTreeAsync(agent.Workspace.Id, tree, "snapshot", agent.Name, null);
		}));
		snapshots[agent.Workspace.Id] = Stopwatch.GetTimestamp();
	}

	/// <summary>Lands the agent's workspace; when mainline refuses it, abandons it and replays the round's changes on a new workspace.</summary>
	private async Task LandAsync(Agent agent, int round, ConcurrentDictionary<string, long> snapshots)
	{
		var landing = new RemoteLanding(client);
		for (var attempt = 0; ; attempt++)
		{
			var result = await recorder.TimeAsync(Operations.Land, () => landing.LandAsync(new LandRequest(agent.Workspace.Id, Agent: agent.Name)),
				landed => $"{Operations.Land} ({landed.Outcome})");
			if (result.Outcome == LandOutcome.Landed)
			{
				return;
			}

			if (result.Outcome == LandOutcome.Nothing)
			{
				// Nothing to land leaves the workspace open; an agent that is done closes it.
				await recorder.TimeAsync(Operations.WorkspaceClose, () => agent.Service.CloseAsync(agent.Workspace.Id, false, agent.Name));
				return;
			}

			if (attempt >= 20)
			{
				throw new InvalidOperationException($"{agent.Name} could not land in round {round}: {result.Outcome}: {result.Message}");
			}

			await recorder.TimeAsync(Operations.WorkspaceClose, () => agent.Service.CloseAsync(agent.Workspace.Id, true, agent.Name));
			var replayed = agent.Workspace;
			agent.Workspace = await recorder.TimeAsync(Operations.WorkspaceCreate,
				() => agent.Service.CreateAsync($"a{agent.Index}-r{round}-replay{attempt}", agent.Name, intent: $"round {round}", replayOf: replayed.Id));
			var repo = agent.Repo;
			await recorder.TimeAsync(Operations.Snapshot, () => repo.Objects.BatchAsync(async () =>
			{
				var working = await repo.Objects.ReadCommitAsync(agent.Workspace.Record.Working);
				var tree = await repo.Trees.EditAsync(working.Tree, agent.Changes);
				return await agent.Service.SetTreeAsync(agent.Workspace.Id, tree, "replay", agent.Name, null);
			}));
			snapshots[agent.Workspace.Id] = Stopwatch.GetTimestamp();
		}
	}

	/// <summary>
	/// Follows the server's watch stream and records, for each workspace, the time from its latest snapshot to the
	/// event that says the server reconciled it with mainline.
	/// </summary>
	private async Task<Task> WatchAsync(ServerConnection connection, ConcurrentDictionary<string, long> snapshots, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, ServerConnection.Path("watch"));
		var response = await connection.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
		await ServerConnection.EnsureAsync(response, ct);
		var samples = recorder.Of(Operations.ReconcileAfterSnapshot);
		return Task.Run(async () =>
		{
			try
			{
				using (request)
				using (response)
				using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct)))
				{
					while (await reader.ReadLineAsync(ct) is { } line)
					{
						if (line.StartsWith("data: ", StringComparison.Ordinal)
							&& JsonSerializer.Deserialize<Api.WatchEvent>(line[6..], Api.Json) is { Kind: "reconciled", Workspace: { } workspace }
							&& snapshots.TryRemove(workspace, out var taken))
						{
							samples.Add(taken, Stopwatch.GetTimestamp(), null);
						}
					}
				}
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
			{
			}
		}, CancellationToken.None);
	}

	/// <summary>
	/// The read operations, each on a client that has cached nothing (a job container that just started), so their cost
	/// is the cost of the state. Returns the number of open workspaces.
	/// </summary>
	private async Task<int> ProbeAsync(ServerConnection connection, ServerApp? server, MainState main, CancellationToken ct)
	{
		log("measuring read operations");
		var open = 0;
		for (var repetition = 0; repetition < scenario.Probes; repetition++)
		{
			open = (await recorder.TimeAsync(Operations.WorkspaceList, () => Client(connection, null).ListWorkspacesAsync(false, ct))).Count;
			if (scenario.Open > 0)
			{
				await recorder.TimeAsync(Operations.WorkspaceResolve, () => Client(connection, null).ResolveWorkspaceAsync(Seeder.OpenName(0), ct));
			}

			await recorder.TimeAsync(Operations.ConflictOverview, () => new RemoteConflictOverview(connection).OverviewAsync(ct));
			if (main.Seq >= 1)
			{
				await recorder.TimeAsync(Operations.MainAtSeq, () => Client(connection, null).MainAtSeqAsync(1, ct));
				await recorder.TimeAsync(Operations.LogPage, () => LogPageAsync(Client(connection, null), main.Seq / 2, ct));
			}

			await recorder.TimeAsync(Operations.LogHead, async () => await Client(connection, null).LogAsync(HeadName.Main, LogPage, ct).ToListAsync(ct));

			if (server is not null)
			{
				// What the server does after every landing: find the workspaces the landing made stale.
				var started = Stopwatch.GetTimestamp();
				var (_, cost) = await ServerProbe.MeasureAsync(() => server.Landing.Reconciler.AffectedAsync(main.Seq, ct));
				recorder.Of(Operations.ReconcileScan).Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds, cost);
			}
		}

		if (server is not null && options.GarbageCollection)
		{
			var started = Stopwatch.GetTimestamp();
			var (report, cost) = await ServerProbe.MeasureAsync(() =>
				new GarbageCollector(server.Repo, server.Maintenance).CollectAsync(TimeSpan.FromHours(24), TimeSpan.FromDays(14), dryRun: true, ct));
			recorder.Of(Operations.GarbageCollection).Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds, cost);
			log($"  garbage collection would keep {report.Live:N0} blobs and {report.Trees:N0} trees");
		}

		return open;
	}

	private const int LogPage = 50;

	/// <summary>A page of mainline's log that starts at a past seq, as Studio's log page reads it.</summary>
	private static async Task<int> LogPageAsync(Repository repo, long from, CancellationToken ct)
	{
		var state = await repo.MainAtSeqAsync(from, ct);
		var operation = state.Operation;
		var read = 1;
		while (read < LogPage && operation.Parents.Count > 0)
		{
			operation = await repo.Objects.ReadOperationAsync(operation.Parents[0], ct);
			read++;
		}

		return read;
	}
}
