namespace Bassia.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Server;
using Bassia.Stores;

/// <summary>
/// What nodes sharing one database tell one another (feat record bassia-1cdec5d0): events and claims through
/// PostgreSQL, and lease waiters woken by a release instead of polling.
/// </summary>
public sealed class ServerEventsTests
{
	[PostgresFact]
	public async Task WhatOneStoreSends_EveryStoreOnTheSchemaHears_AndNoOtherSchemaDoes()
	{
		var connection = Backends.RequiredPostgres();
		await using var one = new PostgresState(connection);
		await using var two = new PostgresState(connection);
		await using var elsewhere = new PostgresState(Backends.RequiredPostgres());
		var heard = new ConcurrentQueue<string>();
		using var first = one.Subscribe(IServerEvents.Events, payload => heard.Enqueue("one:" + payload));
		using var second = two.Subscribe(IServerEvents.Events, payload => heard.Enqueue("two:" + payload));
		using var other = elsewhere.Subscribe(IServerEvents.Events, payload => heard.Enqueue("elsewhere:" + payload));
		using var leases = two.Subscribe(IServerEvents.Leases, payload => heard.Enqueue("leases:" + payload));
		await Task.WhenAll(one.ListeningAsync(), two.ListeningAsync(), elsewhere.ListeningAsync());

		await one.NotifyAsync(IServerEvents.Events, "hello");
		await WaitAsync(() => heard.Count >= 2);
		await Task.Delay(100);
		Assert.Equal(["one:hello", "two:hello"], heard.Order());

		// A subscription that ended hears nothing more.
		second.Dispose();
		await two.NotifyAsync(IServerEvents.Events, "again");
		await WaitAsync(() => heard.Count >= 3);
		await Task.Delay(100);
		Assert.Equal(["one:again", "one:hello", "two:hello"], heard.Order());
	}

	[PostgresFact]
	public async Task AClaim_IsHeldByOneStoreAtATime()
	{
		var connection = Backends.RequiredPostgres();
		await using var one = new PostgresState(connection);
		await using var two = new PostgresState(connection);
		var held = await one.TryLockAsync("reconcile/w1");
		Assert.NotNull(held);
		Assert.Null(await two.TryLockAsync("reconcile/w1"));
		await using (var other = await two.TryLockAsync("reconcile/w2"))
		{
			Assert.NotNull(other);
		}

		await held.DisposeAsync();
		await using var after = await two.TryLockAsync("reconcile/w1");
		Assert.NotNull(after);
	}

	internal static async Task WaitAsync(Func<bool> condition, int seconds = 30)
	{
		var watch = Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), "The condition was not reached in time.");
			await Task.Delay(20);
		}
	}

	/// <summary>Counts the PostgreSQL commands issued while it lives.</summary>
	private sealed class StatementCounter : IDisposable
	{
		private readonly ActivityListener listener;
		private int count;

		public StatementCounter()
		{
			listener = new ActivityListener
			{
				ShouldListenTo = source => source.Name == "Npgsql",
				Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
				ActivityStopped = activity =>
				{
					if (activity.TagObjects.Any(tag => tag.Key is "db.query.text" or "db.statement" or "db.operation.name"))
					{
						Interlocked.Increment(ref count);
					}
				}
			};
			ActivitySource.AddActivityListener(listener);
		}

		public int Count => Volatile.Read(ref count);

		public void Dispose() => listener.Dispose();
	}

	[PostgresFact]
	public async Task ALeaseWaiter_IsWokenByTheRelease_OnAnotherNode_AndHardlyAsksMeanwhile()
	{
		var connection = Backends.RequiredPostgres();
		await using var holderNode = new PostgresState(connection);
		await using var waiterNode = new PostgresState(connection);
		var held = (await holderNode.AcquireAsync(new LeaseRequest("src/billing", LeaseMode.Exclusive, "w1", TimeSpan.FromMinutes(5)))).Lease!;

		int statements;
		Task<LeaseResult> waiting;
		using (var counter = new StatementCounter())
		{
			waiting = waiterNode.AcquireAsync(new LeaseRequest("src/billing/api", LeaseMode.Exclusive, "w2", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60))).AsTask();
			await Task.Delay(2000);
			Assert.False(waiting.IsCompleted);
			statements = counter.Count;
		}

		// Two seconds of waiting: a look every half second, not ten a second (which was 20 transactions of six statements).
		Assert.InRange(statements, 1, 60);

		var released = Stopwatch.StartNew();
		await holderNode.ReleaseAsync(held.Id, held.Token);
		var result = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(result.Granted);
		Assert.True(released.ElapsedMilliseconds < 400, $"granted {released.ElapsedMilliseconds} ms after the release");
	}

	[PostgresFact]
	public async Task AGrant_LooksOnlyAtLeasesThatOverlap()
	{
		await using var state = new PostgresState(Backends.RequiredPostgres());
		LeaseRequest Request(string prefix, string owner) => new(prefix, LeaseMode.Exclusive, owner, TimeSpan.FromMinutes(5));
		Assert.True((await state.AcquireAsync(Request("src/billing/api", "w1"))).Granted);
		Assert.True((await state.AcquireAsync(Request("docs", "w2"))).Granted);
		Assert.True((await state.AcquireAsync(Request("src/billingx", "w3"))).Granted); // a sibling whose name starts the same

		// An ancestor, the prefix itself and a descendant stand in the way; a sibling and an unrelated prefix do not.
		Assert.Equal(["src/billing/api/"], (await state.AcquireAsync(Request("src", "w4"))).Blocking.Where(lease => lease.Prefix.StartsWith("src/billing/", StringComparison.Ordinal)).Select(lease => lease.Prefix));
		Assert.Equal(["src/billing/api/"], (await state.AcquireAsync(Request("src/billing", "w4"))).Blocking.Select(lease => lease.Prefix));
		Assert.Equal(["src/billing/api/"], (await state.AcquireAsync(Request("src/billing/api", "w4"))).Blocking.Select(lease => lease.Prefix));
		Assert.Equal(["src/billing/api/"], (await state.AcquireAsync(Request("src/billing/api/v1", "w4"))).Blocking.Select(lease => lease.Prefix));
		Assert.True((await state.AcquireAsync(Request("src/billing/web", "w4"))).Granted);
		Assert.True((await state.AcquireAsync(Request("src/other", "w5"))).Granted);
		Assert.Equal(3, (await state.AcquireAsync(Request("", "w6"))).Blocking.Count(lease => lease.Owner is "w1" or "w2" or "w3"));
	}
}

/// <summary>Two <c>bassia-server</c> nodes on one database and one blob folder.</summary>
public sealed class ServerNodesTests : IAsyncLifetime
{
	private readonly TempDirectory temp = new();
	private ServerApp nodeA = null!;
	private ServerApp nodeB = null!;
	private const string Token = "s3cret";

	public async Task InitializeAsync()
	{
		var postgres = Backends.RequiredPostgres();
		ServerApp Node() => new(new ServerOptions { Data = temp.Combine("shared"), Postgres = postgres, Url = "http://127.0.0.1:0", Token = Token, SemanticDriver = "off" });
		nodeA = Node();
		await nodeA.StartAsync();
		nodeB = Node();
		await nodeB.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await nodeA.DisposeAsync();
		await nodeB.DisposeAsync();
		temp.Dispose();
	}

	private static Repository Client(ServerConnection connection) =>
		new(new CachingObjectStore(new MemoryObjectStore(), new RemoteObjectStore(connection)), new RemoteViewStore(connection), new RemotePathIndex(connection),
			new RemoteLeaseStore(connection), null, new CachingTreeStore(new MemoryTreeStore(), new RemoteTreeStore(connection)), new RemoteWorkspaceIndex(connection));

	/// <summary>Follows a node's watch stream into a queue until cancelled.</summary>
	private static async Task<(ConcurrentQueue<Api.WatchEvent> Events, Task Reading)> WatchAsync(ServerConnection connection, CancellationToken ct)
	{
		var events = new ConcurrentQueue<Api.WatchEvent>();
		var response = await connection.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, ServerConnection.Path("watch")), HttpCompletionOption.ResponseHeadersRead, ct);
		response.EnsureSuccessStatusCode();
		var reading = Task.Run(async () =>
		{
			try
			{
				using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
				while (await reader.ReadLineAsync(ct) is { } line)
				{
					if (line.StartsWith("data: ", StringComparison.Ordinal))
					{
						events.Enqueue(JsonSerializer.Deserialize<Api.WatchEvent>(line[6..], Api.Json)!);
					}
				}
			}
			catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
			{
			}
		}, CancellationToken.None);
		return (events, reading);
	}

	[PostgresFact]
	public async Task AWatcherOfOneNode_SeesWhatHappensOnTheOther_AndAWorkspaceIsReconciledOnce()
	{
		using var toA = new ServerConnection(nodeA.Address!, Token);
		using var toB = new ServerConnection(nodeB.Address!, Token);
		using var stop = new CancellationTokenSource();
		var (onB, readingB) = await WatchAsync(toB, stop.Token);
		var (onA, readingA) = await WatchAsync(toA, stop.Token);

		// A client of node A creates a workspace, snapshots and lands; nothing of it goes through node B.
		var repo = Client(toA);
		var service = new WorkspaceService(repo);
		var workspace = await service.CreateAsync("feature", "ann");
		var working = await repo.Objects.ReadCommitAsync(workspace.Record.Working);
		var tree = await repo.Trees.EditAsync(working.Tree, [new PathChange("src/a.txt", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes("a\n")))]);
		await service.SetTreeAsync(workspace.Id, tree, "snapshot", "ann", null);
		await ServerEventsTests.WaitAsync(() => onB.Any(change => change.Kind == "reconciled" && change.Workspace == workspace.Id));
		var landed = await new RemoteLanding(toA).LandAsync(new LandRequest(workspace.Id));
		Assert.Equal(LandOutcome.Landed, landed.Outcome);

		// Node B's watcher sees the workspace move and the landing, with what node A's watcher sees.
		await ServerEventsTests.WaitAsync(() => onB.Any(change => change.Kind == "landed" && change.Seq == landed.Seq) && onA.Any(change => change.Kind == "landed"));
		await ServerEventsTests.WaitAsync(() => nodeA.Reconciling.Idle && nodeB.Reconciling.Idle);
		await Task.Delay(300);
		Assert.Contains(onB, change => change.Kind == "workspace" && change.Workspace == workspace.Id);
		Assert.Equal(["src"], onB.Single(change => change.Kind == "landed").Prefixes);
		Assert.Equal(onA.Where(change => change.Workspace == workspace.Id).Select(change => change.Kind).Order(),
			onB.Where(change => change.Workspace == workspace.Id).Select(change => change.Kind).Order());

		// Each event once per watcher: node A's own events do not come back to its watchers a second time.
		Assert.Single(onA, change => change.Kind == "landed");
		Assert.Single(onB, change => change.Kind == "landed");

		// A client of node B sees the same mainline and the same workspace.
		Assert.Equal(landed.Seq, (await Client(toB).GetMainAsync()).Seq);
		Assert.Equal(WorkspaceStatus.Landed, (await Client(toB).ResolveWorkspaceAsync(workspace.Id)).Record.Status);

		await stop.CancelAsync();
		await Task.WhenAll(readingA, readingB);
	}

	[PostgresFact]
	public async Task AWorkspaceNoNodeWasToldAbout_IsFoundByTheNextLook()
	{
		// A workspace whose head moves without an event reaching any node's queue: as when the node that queued it died.
		var repo = new Repository(nodeA.Blobs, nodeA.State, nodeA.State, nodeA.State, null, nodeA.Trees, nodeA.State);
		var service = new WorkspaceService(repo);
		var workspace = await service.CreateAsync("orphan", "ann");
		var working = await repo.Objects.ReadCommitAsync(workspace.Record.Working);
		var tree = await repo.Trees.EditAsync(working.Tree, [new PathChange("src/b.txt", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes("b\n")))]);
		await service.SetTreeAsync(workspace.Id, tree, "snapshot", "ann", null);
		Assert.Null(await nodeB.Landing.Reconciler.GetAsync(workspace.Id));
		Assert.Contains(workspace.Id, await nodeB.State.StaleAsync((await repo.GetMainAsync()).Seq, RepoPolicy.FileName));

		// Either node's look (every 30 s; asked for here) picks it up, and one of them reconciles it.
		nodeA.Reconciling.LookAgain();
		nodeB.Reconciling.LookAgain();
		await ServerEventsTests.WaitAsync(() => nodeA.Reconciling.Idle && nodeB.Reconciling.Idle);
		var candidate = await nodeB.Landing.Reconciler.GetAsync(workspace.Id);
		Assert.NotNull(candidate);
		Assert.Equal(LandOutcome.WouldLand, candidate.Outcome);
		Assert.DoesNotContain(workspace.Id, await nodeA.State.StaleAsync((await repo.GetMainAsync()).Seq, RepoPolicy.FileName));
	}

	[PostgresFact]
	public async Task AWorkspaceAnotherNodeHasClaimed_IsLeftToIt_AndLookedAtAgain()
	{
		var repo = Client(new ServerConnection(nodeA.Address!, Token));
		var workspace = await new WorkspaceService(repo).CreateAsync("claimed", "ann");
		await ServerEventsTests.WaitAsync(() => nodeA.Reconciling.Idle && nodeB.Reconciling.Idle);
		var before = await nodeA.State.GetHeadAsync(HeadName.Candidate(workspace.Id));

		// Node B holds the claim (as if it were reconciling); node A is asked to reconcile and waits its turn.
		var claim = await nodeB.State.TryLockAsync("reconcile/" + workspace.Id);
		Assert.NotNull(claim);
		var working = await repo.Objects.ReadCommitAsync(workspace.Record.Working);
		var tree = await repo.Trees.EditAsync(working.Tree, [new PathChange("src/c.txt", await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes("c\n")))]);
		await new WorkspaceService(repo).SetTreeAsync(workspace.Id, tree, "snapshot", "ann", null);
		await Task.Delay(700);
		Assert.False(nodeA.Reconciling.Idle);
		Assert.Equal(before, await nodeA.State.GetHeadAsync(HeadName.Candidate(workspace.Id)));

		await claim.DisposeAsync();
		await ServerEventsTests.WaitAsync(() => nodeA.Reconciling.Idle);
		var candidate = await nodeA.Landing.Reconciler.GetAsync(workspace.Id);
		Assert.Contains("src/c.txt", candidate!.Touched);
	}
}
