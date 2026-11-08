namespace Bassia.Tests;

using System.Text;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;
using Bassia.Stores;
using Bassia.Tests.Core;
using Npgsql;

/// <summary>
/// Schema versioning of the PostgreSQL stores (bassia-5eee7287): a fresh schema, tables made before versioning
/// (baselined, or refused when they differ), later steps in order, a step that fails, concurrent starters, a database
/// newer than the build, the clone's cache that is rebuilt instead of refused, and the path index rebuilt from the
/// operation log. Each test gets a schema of its own; all need PostgreSQL (<c>BASSIA_TEST_POSTGRES</c>).
/// </summary>
public sealed class SchemaMigrationTests
{
	/// <summary>What builds before schema versioning ran on every start, verbatim: the tables existing databases have.</summary>
	private const string PreVersioningSql = """
		CREATE TABLE IF NOT EXISTS bassia_heads (name text COLLATE "C" PRIMARY KEY, head text NOT NULL);
		CREATE TABLE IF NOT EXISTS bassia_paths (path text COLLATE "C" NOT NULL, seq bigint NOT NULL, kind integer, id text, size bigint NOT NULL,
			exec boolean NOT NULL, PRIMARY KEY (path, seq));
		CREATE INDEX IF NOT EXISTS bassia_paths_seq ON bassia_paths (seq);
		CREATE TABLE IF NOT EXISTS bassia_meta (key text PRIMARY KEY, value bigint NOT NULL);
		CREATE TABLE IF NOT EXISTS bassia_leases (id text PRIMARY KEY, prefix text COLLATE "C" NOT NULL, mode integer NOT NULL, owner text NOT NULL,
			run text, token bigint NOT NULL, granted timestamptz NOT NULL, expires timestamptz NOT NULL);
		CREATE TABLE IF NOT EXISTS bassia_lease_queue (ticket bigserial PRIMARY KEY, prefix text COLLATE "C" NOT NULL, mode integer NOT NULL,
			owner text NOT NULL, alive_until timestamptz NOT NULL);
		CREATE SEQUENCE IF NOT EXISTS bassia_fencing;
		CREATE TABLE IF NOT EXISTS bassia_trees (id bytea PRIMARY KEY, written timestamptz NOT NULL DEFAULT now());
		CREATE TABLE IF NOT EXISTS bassia_tree_entries (tree_id bytea NOT NULL, name text COLLATE "C" NOT NULL, ord integer NOT NULL, kind smallint NOT NULL,
			child_id bytea NOT NULL, size bigint NOT NULL, exec boolean NOT NULL, PRIMARY KEY (tree_id, name));
		""";

	private static readonly ObjectId SomeHead = ObjectId.Of([1, 2, 3]);

	private static void Sql(string connection, string sql)
	{
		using var db = new NpgsqlConnection(connection);
		db.Open();
		using var command = new NpgsqlCommand(sql, db);
		command.ExecuteNonQuery();
	}

	private static object? Scalar(string connection, string sql)
	{
		using var db = new NpgsqlConnection(connection);
		db.Open();
		using var command = new NpgsqlCommand(sql, db);
		return command.ExecuteScalar();
	}

	private static bool TableExists(string connection, string table) =>
		Scalar(connection, $"SELECT to_regclass(quote_ident(current_schema()) || '.{table}') IS NOT NULL") is true;

	private static (int Version, int CompatibleFrom)? Stored(string connection, string component)
	{
		if (!TableExists(connection, "bassia_schema"))
		{
			return null;
		}

		using var db = new NpgsqlConnection(connection);
		db.Open();
		using var command = new NpgsqlCommand($"SELECT version, compatible_from FROM bassia_schema WHERE component = '{component}'", db);
		using var reader = command.ExecuteReader();
		return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
	}

	private static SchemaStatus Run(string connection, SchemaComponent component, SchemaMode mode = SchemaMode.Migrate, TimeSpan? lockTimeout = null)
	{
		using var source = NpgsqlDataSource.Create(connection);
		return SchemaMigrator.Run(source, component, mode, lockTimeout);
	}

	/// <summary>A component of the tests' own, to exercise steps after 1 without changing the stores' real schema.</summary>
	private static SchemaComponent Widgets(params SchemaMigration[] later) => new("widgets",
		[new SchemaMigration(1, "widgets", "CREATE TABLE IF NOT EXISTS widgets (id integer PRIMARY KEY, name text NOT NULL)"), .. later]);

	// ----- a fresh schema -----

	[PostgresFact]
	public async Task FreshSchema_IsCreatedAtTheLatestVersion_AndTheNextOpenCostsNothing()
	{
		var connection = Backends.RequiredPostgres();
		await using (var state = new PostgresState(connection))
		await using (var trees = new PostgresTreeStore(connection))
		{
			Assert.Equal(SchemaOutcome.Created, state.SchemaStatus.Outcome);
			Assert.Equal(SchemaOutcome.Created, trees.SchemaStatus.Outcome);
			Assert.Equal([1], state.SchemaStatus.Applied);
			Assert.True((await state.AdvanceHeadAsync(new HeadName("main"), null, SomeHead)).Success);
		}

		Assert.Equal((PostgresState.Schema.Latest, 1), Stored(connection, "state"));
		Assert.Equal((PostgresTreeStore.Schema.Latest, 1), Stored(connection, "trees"));
		Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM bassia_schema_history"));

		await using var again = new PostgresState(connection);
		Assert.Equal(SchemaOutcome.Current, again.SchemaStatus.Outcome);
		Assert.Empty(again.SchemaStatus.Applied);
		Assert.Equal(SomeHead, await again.GetHeadAsync(new HeadName("main")));
	}

	// ----- baselining -----

	[PostgresFact]
	public async Task PreVersioningTables_AreStampedAsVersion1_WithTheirDataUntouched()
	{
		var connection = Backends.RequiredPostgres();
		Sql(connection, PreVersioningSql);
		Sql(connection, $"INSERT INTO bassia_heads VALUES ('main', '{SomeHead.Hex}'); INSERT INTO bassia_meta VALUES ('indexed_seq', 7); SELECT nextval('bassia_fencing'), nextval('bassia_fencing');");

		await using (var state = new PostgresState(connection))
		await using (var trees = new PostgresTreeStore(connection))
		{
			Assert.Equal(SchemaOutcome.Baselined, state.SchemaStatus.Outcome);
			Assert.Equal(SchemaOutcome.Baselined, trees.SchemaStatus.Outcome);
			Assert.Equal(SomeHead, await state.GetHeadAsync(new HeadName("main")));
			Assert.Equal(7, await state.IndexedSeqAsync());
		}

		// The fencing sequence kept counting: a baseline never recreates it.
		Assert.Equal(3L, Scalar(connection, "SELECT nextval('bassia_fencing')"));
		Assert.Equal((1, 1), Stored(connection, "state"));
		Assert.StartsWith("baseline", (string)Scalar(connection, "SELECT description FROM bassia_schema_history WHERE component = 'state' AND version = 1")!);

		await using var again = new PostgresState(connection);
		Assert.Equal(SchemaOutcome.Current, again.SchemaStatus.Outcome);
	}

	[PostgresFact]
	public async Task PartlyCreatedPreVersioningSchema_IsCompletedByTheBaseline()
	{
		// A start that died halfway through the old script: some tables, no index, no sequence.
		var connection = Backends.RequiredPostgres();
		Sql(connection, PreVersioningSql.Split("CREATE INDEX")[0]);
		await using var state = new PostgresState(connection);
		Assert.Equal(SchemaOutcome.Baselined, state.SchemaStatus.Outcome);
		Assert.Equal(1L, Scalar(connection, "SELECT nextval('bassia_fencing')"));
		Assert.True(TableExists(connection, "bassia_paths_seq"));
	}

	[PostgresFact]
	public void PreVersioningTables_ThatDifferFromVersion1_AreRefused_AndLeftAsTheyAre()
	{
		var connection = Backends.RequiredPostgres();
		Sql(connection, PreVersioningSql.Replace("seq bigint NOT NULL, kind integer", "seq bigint NOT NULL, kind text").Replace("CREATE TABLE IF NOT EXISTS bassia_heads (name text COLLATE \"C\" PRIMARY KEY",
			"CREATE TABLE IF NOT EXISTS bassia_heads (name text PRIMARY KEY"));

		var error = Assert.Throws<SchemaException>(() => new PostgresState(connection));
		Assert.Contains("bassia_paths.kind is text (expected integer)", error.Message);
		Assert.Contains("bassia_heads.name is text NOT NULL (expected text COLLATE \"C\" NOT NULL)", error.Message);
		Assert.Null(Stored(connection, "state"));
		Assert.Equal("text", Scalar(connection, "SELECT data_type FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'bassia_paths' AND column_name = 'kind'"));
	}

	// ----- later steps -----

	[PostgresFact]
	public void LaterSteps_RunInOrder_OnceEach_AndExpandKeepsOlderBuildsCompatible()
	{
		var connection = Backends.RequiredPostgres();
		Assert.Equal(SchemaOutcome.Created, Run(connection, Widgets()).Outcome);
		Sql(connection, "INSERT INTO widgets VALUES (1, 'gear')");

		// Expand: a column with a default, which a build at version 1 neither reads nor has to write.
		var expand = new SchemaMigration(2, "widget colour", "ALTER TABLE widgets ADD COLUMN colour text NOT NULL DEFAULT 'grey'");
		var expanded = Run(connection, Widgets(expand));
		Assert.Equal((SchemaOutcome.Migrated, 2, 1), (expanded.Outcome, expanded.Version, expanded.CompatibleFrom));
		Assert.Equal([2], expanded.Applied);
		Assert.Equal("grey", Scalar(connection, "SELECT colour FROM widgets WHERE id = 1"));

		// The build at version 1 still opens it, mid rolling deploy.
		Assert.Equal(SchemaOutcome.NewerCompatible, Run(connection, Widgets(), SchemaMode.Verify).Outcome);

		// Contract: the name column goes, so builds that know only version 1 (and still read it) must stop.
		var contract = new SchemaMigration(3, "drop widget name", "ALTER TABLE widgets DROP COLUMN name") { MinimumCode = 2 };
		var contracted = Run(connection, Widgets(expand, contract));
		Assert.Equal((3, 2), (contracted.Version, contracted.CompatibleFrom));
		Assert.Equal([3], contracted.Applied);
		Assert.Equal(SchemaOutcome.NewerCompatible, Run(connection, Widgets(expand)).Outcome);
		Assert.Contains("Upgrade Bassia", Assert.Throws<SchemaException>(() => Run(connection, Widgets())).Message);
		Assert.Equal(3L, Scalar(connection, "SELECT count(*) FROM bassia_schema_history WHERE component = 'widgets'"));
	}

	[PostgresFact]
	public void FailingStep_IsRolledBack_AndTheVersionStays()
	{
		var connection = Backends.RequiredPostgres();
		Run(connection, Widgets());
		var broken = new SchemaMigration(2, "half a change", "ALTER TABLE widgets ADD COLUMN colour text; SELECT 1 / 0");

		var error = Assert.Throws<SchemaException>(() => Run(connection, Widgets(broken)));
		Assert.Contains("Migration 2 of the widgets schema", error.Message);
		Assert.Equal((1, 1), Stored(connection, "widgets"));
		Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'widgets' AND column_name = 'colour'"));

		// A fixed step applies on the next start.
		Assert.Equal([2], Run(connection, Widgets(broken with { Sql = "ALTER TABLE widgets ADD COLUMN colour text" })).Applied);
	}

	[Fact]
	public void Steps_MustBeNumberedFromOne_WithoutGaps()
	{
		using var source = NpgsqlDataSource.Create("Host=unused");
		Assert.Throws<ArgumentException>(() => SchemaMigrator.Run(source, Widgets(new SchemaMigration(3, "gap", "SELECT 1"))));
	}

	// ----- concurrency -----

	[PostgresFact]
	public async Task ConcurrentStarters_ApplyEachStepOnce()
	{
		var connection = Backends.RequiredPostgres();

		// Step 2 is not idempotent (a second run fails on the existing table) and slow, so starters overlap inside it.
		var slow = Widgets(new SchemaMigration(2, "slow and not idempotent", "CREATE TABLE once (n integer); SELECT pg_sleep(0.3)"));
		using var start = new ManualResetEventSlim();
		var starters = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
		{
			start.Wait();
			return Run(connection, slow);
		})).ToList();
		start.Set();
		var statuses = await Task.WhenAll(starters);

		Assert.All(statuses, status => Assert.Equal(2, status.Version));
		Assert.Equal([1, 2], statuses.SelectMany(status => status.Applied).Order());
		Assert.Single(statuses, status => status.Outcome == SchemaOutcome.Created);
		Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM bassia_schema_history WHERE component = 'widgets'"));
	}

	[PostgresFact]
	public async Task ConcurrentServerNodes_StartOnOneFreshSchema()
	{
		var connection = Backends.RequiredPostgres();
		var nodes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => (new PostgresState(connection), new PostgresTreeStore(connection)))));
		Assert.Single(nodes, node => node.Item1.SchemaStatus.Outcome == SchemaOutcome.Created);
		Assert.Single(nodes, node => node.Item2.SchemaStatus.Outcome == SchemaOutcome.Created);
		foreach (var (state, trees) in nodes)
		{
			await state.DisposeAsync();
			await trees.DisposeAsync();
		}
	}

	[PostgresFact]
	public void StarterWaitingTooLongForTheLock_GivesUp()
	{
		var connection = Backends.RequiredPostgres();
		using var holder = new NpgsqlConnection(connection);
		holder.Open();
		using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(1650553715, hashtext(current_schema()))", holder))
		{
			take.ExecuteNonQuery();
		}

		var error = Assert.Throws<SchemaException>(() => Run(connection, Widgets(), lockTimeout: TimeSpan.FromMilliseconds(300)));
		Assert.Contains("Another process", error.Message);
		Assert.False(TableExists(connection, "widgets"));
	}

	// ----- a database newer than the build -----

	[PostgresFact]
	public async Task DatabaseNewerThanTheBuild_IsRefused_UnlessItSaysTheBuildCanStillUseIt()
	{
		var connection = Backends.RequiredPostgres();
		await (new PostgresState(connection)).DisposeAsync();
		var latest = PostgresState.Schema.Latest;

		// An expand step of a later build: this build keeps running.
		Sql(connection, $"UPDATE bassia_schema SET version = {latest + 1}, compatible_from = {latest} WHERE component = 'state'");
		await using (var state = new PostgresState(connection, SchemaMode.Verify))
		{
			Assert.Equal(SchemaOutcome.NewerCompatible, state.SchemaStatus.Outcome);
		}

		// A contract step of a later build: this build must not start, in either mode, and changes nothing.
		Sql(connection, $"UPDATE bassia_schema SET compatible_from = {latest + 1} WHERE component = 'state'");
		foreach (var mode in new[] { SchemaMode.Migrate, SchemaMode.Verify })
		{
			var error = Assert.Throws<SchemaException>(() => new PostgresState(connection, mode));
			Assert.Contains($"needs a build that knows version {latest + 1}", error.Message);
		}

		Assert.Equal((latest + 1, latest + 1), Stored(connection, "state"));
	}

	// ----- verify mode -----

	[PostgresFact]
	public async Task VerifyMode_ChangesNothing_AndRefusesAnOutdatedSchema()
	{
		var connection = Backends.RequiredPostgres();
		Assert.Contains("not versioned yet", Assert.Throws<SchemaException>(() => new PostgresState(connection, SchemaMode.Verify)).Message);
		Assert.False(TableExists(connection, "bassia_heads"));
		Assert.False(TableExists(connection, "bassia_schema"));

		Sql(connection, PreVersioningSql);
		Assert.Throws<SchemaException>(() => new PostgresTreeStore(connection, SchemaMode.Verify));
		Assert.Null(Stored(connection, "trees"));

		Assert.Equal(2, SchemaMigrator.RunAll(connection, SchemaMigrator.ServerComponents).Count);
		await using var state = new PostgresState(connection, SchemaMode.Verify);
		await using var trees = new PostgresTreeStore(connection, SchemaMode.Verify);
		Assert.Equal(SchemaOutcome.Current, state.SchemaStatus.Outcome);
		Assert.Equal(SchemaOutcome.Current, trees.SchemaStatus.Outcome);
	}

	[PostgresFact]
	public async Task ServerMigrateCommand_MigratesAndChecks_WithoutADataFolder()
	{
		var connection = Backends.RequiredPostgres();
		Assert.Equal(1, await Bassia.Server.Program.Main(["migrate", "-postgres", connection, "-check"]));
		Assert.Equal(0, await Bassia.Server.Program.Main(["migrate", "-postgres", connection]));
		Assert.Equal(0, await Bassia.Server.Program.Main(["migrate", "-postgres", connection, "-check"]));
		Assert.Equal((1, 1), Stored(connection, "state"));
		Assert.Equal((1, 1), Stored(connection, "trees"));
	}

	// ----- the clone's cache -----

	[PostgresFact]
	public async Task CloneCache_ThatThisBuildCannotUse_IsDroppedAndRebuilt()
	{
		var connection = Backends.RequiredPostgres();
		var tree = Tree.Of([new TreeEntry("a", EntryKind.File, ObjectId.Of([7]), 1)]);
		var id = ObjectId.Of(ObjectCodec.Encode(tree));

		// Tables of an unknown shape: a primary store refuses them, the cache starts over.
		Sql(connection, "CREATE TABLE bassia_trees (id text PRIMARY KEY); CREATE TABLE bassia_tree_entries (tree_id text)");
		Assert.Throws<SchemaException>(() => new PostgresTreeStore(connection));
		await using (var cache = new PostgresTreeStore(connection, cache: true))
		{
			Assert.Equal(SchemaOutcome.Rebuilt, cache.SchemaStatus.Outcome);
			await cache.PutManyAsync([(id, tree)]);
		}

		// Written by a later build that this one cannot read: dropped again, even in verify mode.
		Sql(connection, "UPDATE bassia_schema SET version = 9, compatible_from = 9 WHERE component = 'trees'");
		await using var rebuilt = new PostgresTreeStore(connection, SchemaMode.Verify, cache: true);
		Assert.Equal(SchemaOutcome.Rebuilt, rebuilt.SchemaStatus.Outcome);
		Assert.Empty(await rebuilt.GetManyAsync([id]));
		Assert.Equal((1, 1), Stored(connection, "trees"));
	}

	// ----- rebuildability -----

	[PostgresFact]
	public async Task PathIndex_IsRebuiltFromTheOperationLogAndTrees()
	{
		var connection = Backends.RequiredPostgres();
		await using var state = new PostgresState(connection);
		await using var trees = new PostgresTreeStore(connection);
		var repo = new Repository(new MemoryObjectStore(), state, state, state, TestRepo.SmallChunks, trees);
		await repo.InitializeAsync("test");
		var landing = new LandingService(repo);
		var workspaces = new WorkspaceService(repo);
		async Task LandAsync(string name, Dictionary<string, string?> edits)
		{
			var workspace = await workspaces.CreateAsync(name, "agent-" + name, null, "change");
			var working = await repo.Objects.ReadCommitAsync(workspace.Record.Working);
			var changes = new List<PathChange>();
			foreach (var (path, text) in edits)
			{
				changes.Add(new PathChange(path, text is null ? null : await repo.Objects.WriteContentAsync(Encoding.UTF8.GetBytes(text))));
			}

			workspace = await workspaces.SetTreeAsync(workspace.Id, await repo.Trees.EditAsync(working.Tree, changes), "edit", null, null);
			Assert.Equal(LandOutcome.Landed, (await landing.LandAsync(new LandRequest(workspace.Id))).Outcome);
		}

		await LandAsync("one", new() { ["src/a.txt"] = "a", ["src/b.txt"] = "b", ["docs/readme.md"] = "hi" });
		await LandAsync("two", new() { ["src/a.txt"] = "a2", ["docs/readme.md"] = null });
		await LandAsync("three", new() { ["src/deep/c.txt"] = "c" });
		await repo.EnsureIndexedAsync(await repo.GetMainAsync());
		const string Rows = "SELECT string_agg(concat_ws(',', path, seq, kind, id, size, exec), ';' ORDER BY path, seq) FROM bassia_paths";
		var before = (string)Scalar(connection, Rows)!;

		Sql(connection, "TRUNCATE bassia_paths; UPDATE bassia_meta SET value = 0 WHERE key = 'indexed_seq'");
		await repo.EnsureIndexedAsync(await repo.GetMainAsync());
		Assert.Equal(before, (string)Scalar(connection, Rows)!);
		Assert.Equal((await repo.GetMainAsync()).Seq, await state.IndexedSeqAsync());
	}
}
