namespace Bassia.Stores;

using System.Runtime.CompilerServices;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Npgsql;
using NpgsqlTypes;

/// <summary>
/// Trees as rows: one row per tree and one per entry, keyed by (tree, name) with the entry's position kept so a tree is
/// rebuilt in canonical order whatever the database's collation. A path lookup, a subtree walk and a reachability
/// question are each one recursive query (<c>WITH RECURSIVE</c>) instead of one blob read per directory. Ids are the
/// hashes of the canonical encoding and can be recomputed from the rows (scrub does).
/// </summary>
public sealed class PostgresTreeStore : ITreeStore, ITreeStoreMaintenance, IAsyncDisposable
{
	private const int TreeKind = 3; // (int)EntryKind.Tree; a literal so SQL text can be constant
	private const int InsertBatch = 20_000;

	private readonly NpgsqlDataSource source;

	public PostgresTreeStore(string connectionString)
	{
		source = NpgsqlDataSource.Create(connectionString);
		using var connection = source.OpenConnection();
		using var command = new NpgsqlCommand("""
			CREATE TABLE IF NOT EXISTS bassia_trees (id bytea PRIMARY KEY, written timestamptz NOT NULL DEFAULT now());
			CREATE TABLE IF NOT EXISTS bassia_tree_entries (tree_id bytea NOT NULL, name text COLLATE "C" NOT NULL, ord integer NOT NULL, kind smallint NOT NULL,
				child_id bytea NOT NULL, size bigint NOT NULL, exec boolean NOT NULL, PRIMARY KEY (tree_id, name));
			""", connection);
		command.ExecuteNonQuery();
	}

	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var found = new HashSet<ObjectId>();
		foreach (var part in ids.Distinct().Chunk(5000))
		{
			await using var command = source.CreateCommand("SELECT id FROM bassia_trees WHERE id = ANY($1)");
			command.Parameters.Add(new NpgsqlParameter { Value = part.Select(id => id.ToBytes()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				found.Add(ObjectId.FromBytes((byte[])reader[0]));
			}
		}

		return ids.Select(found.Contains).ToList();
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetManyAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var result = new Dictionary<ObjectId, Tree>();
		foreach (var part in ids.Distinct().Chunk(2000))
		{
			await using var command = source.CreateCommand(Select("SELECT id FROM bassia_trees WHERE id = ANY($1)"));
			command.Parameters.Add(new NpgsqlParameter { Value = part.Select(id => id.ToBytes()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
			await ReadTreesAsync(command, result, ct);
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetSubtreesAsync(ObjectId root, int depth, int limit, CancellationToken ct = default)
	{
		// UNION (not UNION ALL) drops a subtree met again at the same depth; the limit bounds what a huge tree brings back.
		await using var command = source.CreateCommand($"""
			WITH RECURSIVE walk(id, depth) AS (
				SELECT id, 0 FROM bassia_trees WHERE id = $1
				UNION
				SELECT e.child_id, w.depth + 1 FROM walk w JOIN bassia_tree_entries e ON e.tree_id = w.id
				WHERE e.kind = {TreeKind} AND w.depth < $2)
			{Select("SELECT DISTINCT id FROM walk LIMIT $3")}
			""");
		command.Parameters.AddWithValue(root.ToBytes());
		command.Parameters.AddWithValue(Math.Min(depth, 10_000));
		command.Parameters.AddWithValue(limit);
		var result = new Dictionary<ObjectId, Tree>();
		await ReadTreesAsync(command, result, ct);
		return result;
	}

	public async Task<IReadOnlyDictionary<ObjectId, Tree>> GetPathAsync(ObjectId root, IReadOnlyList<string> names, CancellationToken ct = default)
	{
		await using var command = source.CreateCommand($"""
			WITH RECURSIVE walk(id, i) AS (
				SELECT id, 0 FROM bassia_trees WHERE id = $1
				UNION ALL
				SELECT e.child_id, w.i + 1 FROM walk w JOIN bassia_tree_entries e ON e.tree_id = w.id AND e.name = ($2)[w.i + 1] AND e.kind = {TreeKind}
				WHERE w.i < cardinality($2))
			{Select("SELECT DISTINCT id FROM walk")}
			""");
		command.Parameters.AddWithValue(root.ToBytes());
		command.Parameters.Add(new NpgsqlParameter { Value = names.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
		var result = new Dictionary<ObjectId, Tree>();
		await ReadTreesAsync(command, result, ct);
		return result;
	}

	/// <summary>Wraps a query that yields tree ids into one that yields their entries (an empty tree comes back as one row without an entry).</summary>
	private static string Select(string ids) => $"""
		SELECT t.id, e.ord, e.name, e.kind, e.child_id, e.size, e.exec
		FROM ({ids}) AS t(id) LEFT JOIN bassia_tree_entries e ON e.tree_id = t.id
		ORDER BY t.id, e.ord
		""";

	private static async Task ReadTreesAsync(NpgsqlCommand command, Dictionary<ObjectId, Tree> into, CancellationToken ct)
	{
		await using var reader = await command.ExecuteReaderAsync(ct);
		ObjectId? current = null;
		var entries = new List<TreeEntry>();
		void Finish()
		{
			if (current is { } id)
			{
				into[id] = new Tree(entries.ToList());
				entries.Clear();
			}
		}

		while (await reader.ReadAsync(ct))
		{
			var id = ObjectId.FromBytes((byte[])reader[0]);
			if (current != id)
			{
				Finish();
				current = id;
			}

			if (!await reader.IsDBNullAsync(2, ct))
			{
				entries.Add(new TreeEntry(reader.GetString(2), (EntryKind)reader.GetInt16(3), ObjectId.FromBytes((byte[])reader[4]), reader.GetInt64(5), reader.GetBoolean(6)));
			}
		}

		Finish();
	}

	public async Task PutManyAsync(IReadOnlyList<(ObjectId Id, Tree Tree)> trees, CancellationToken ct = default)
	{
		foreach (var batch in Batches(trees.DistinctBy(item => item.Id).ToList()))
		{
			await using var connection = await source.OpenConnectionAsync(ct);
			await using var transaction = await connection.BeginTransactionAsync(ct);

			// Only trees this call inserts get entries; a tree stored before (or by a concurrent call) already has its own.
			var inserted = new List<byte[]>();
			await using (var insert = new NpgsqlCommand("INSERT INTO bassia_trees (id) SELECT unnest($1::bytea[]) ON CONFLICT DO NOTHING RETURNING id", connection, transaction))
			{
				insert.Parameters.Add(new NpgsqlParameter { Value = batch.Select(item => item.Id.ToBytes()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
				await using var reader = await insert.ExecuteReaderAsync(ct);
				while (await reader.ReadAsync(ct))
				{
					inserted.Add((byte[])reader[0]);
				}
			}

			var fresh = inserted.Select(bytes => ObjectId.FromBytes(bytes)).ToHashSet();
			var rows = batch.Where(item => fresh.Contains(item.Id))
				.SelectMany(item => item.Tree.Entries.Select((entry, ord) => (Tree: item.Id.ToBytes(), Entry: entry, Ord: ord))).ToList();
			foreach (var part in rows.Chunk(InsertBatch))
			{
				await using var command = new NpgsqlCommand("""
					INSERT INTO bassia_tree_entries (tree_id, name, ord, kind, child_id, size, exec)
					SELECT * FROM unnest($1::bytea[], $2::text[], $3::int[], $4::smallint[], $5::bytea[], $6::bigint[], $7::boolean[]) ON CONFLICT DO NOTHING
					""", connection, transaction);
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => row.Tree).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => row.Entry.Name).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => row.Ord).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => (short)row.Entry.Kind).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => row.Entry.Id.ToBytes()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => row.Entry.Size).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
				command.Parameters.Add(new NpgsqlParameter { Value = part.Select(row => row.Entry.Executable).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean });
				await command.ExecuteNonQueryAsync(ct);
			}

			await transaction.CommitAsync(ct);
		}
	}

	/// <summary>Groups trees so that one insert carries a bounded number of entries.</summary>
	private static IEnumerable<List<(ObjectId Id, Tree Tree)>> Batches(List<(ObjectId Id, Tree Tree)> trees)
	{
		var batch = new List<(ObjectId Id, Tree Tree)>();
		var entries = 0;
		foreach (var item in trees)
		{
			if (batch.Count > 0 && (entries + item.Tree.Entries.Count > InsertBatch * 5 || batch.Count >= 5000))
			{
				yield return batch;
				batch = [];
				entries = 0;
			}

			batch.Add(item);
			entries += item.Tree.Entries.Count;
		}

		if (batch.Count > 0)
		{
			yield return batch;
		}
	}

	public async Task<TreeReach> ReachAsync(IReadOnlyList<ObjectId> roots, CancellationToken ct = default)
	{
		var reach = new TreeReach([], []);
		if (roots.Count == 0)
		{
			return reach;
		}

		var Walk = $"""
			WITH RECURSIVE walk(id) AS (
				SELECT id FROM bassia_trees WHERE id = ANY($1)
				UNION
				SELECT e.child_id FROM walk w JOIN bassia_tree_entries e ON e.tree_id = w.id WHERE e.kind = {TreeKind})
			""";
		var rootBytes = roots.Distinct().Select(id => id.ToBytes()).ToArray();
		await using (var command = source.CreateCommand(Walk + " SELECT id FROM walk"))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = rootBytes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				reach.Trees.Add(ObjectId.FromBytes((byte[])reader[0]));
			}
		}

		await using (var command = source.CreateCommand(Walk + $" SELECT DISTINCT e.kind, e.child_id, e.size, e.exec FROM walk w JOIN bassia_tree_entries e ON e.tree_id = w.id WHERE e.kind <> {TreeKind}"))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = rootBytes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
			await using var reader = await command.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
			{
				reach.Leaves.Add(new Leaf((EntryKind)reader.GetInt16(0), ObjectId.FromBytes((byte[])reader[1]), reader.GetInt64(2), reader.GetBoolean(3)));
			}
		}

		return reach;
	}

	public async IAsyncEnumerable<TreeInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		await using var command = source.CreateCommand("SELECT id, written FROM bassia_trees");
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			yield return new TreeInfo(ObjectId.FromBytes((byte[])reader[0]), reader.GetFieldValue<DateTimeOffset>(1));
		}
	}

	public async Task DeleteAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		foreach (var part in ids.Chunk(2000))
		{
			await using var connection = await source.OpenConnectionAsync(ct);
			await using var transaction = await connection.BeginTransactionAsync(ct);
			var bytes = new NpgsqlParameter { Value = part.Select(id => id.ToBytes()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea };
			await using (var entries = new NpgsqlCommand("DELETE FROM bassia_tree_entries WHERE tree_id = ANY($1)", connection, transaction))
			{
				entries.Parameters.Add(bytes);
				await entries.ExecuteNonQueryAsync(ct);
			}

			await using (var trees = new NpgsqlCommand("DELETE FROM bassia_trees WHERE id = ANY($1)", connection, transaction))
			{
				trees.Parameters.Add(new NpgsqlParameter { Value = bytes.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
				await trees.ExecuteNonQueryAsync(ct);
			}

			await transaction.CommitAsync(ct);
		}
	}

	public ValueTask DisposeAsync() => source.DisposeAsync();
}
