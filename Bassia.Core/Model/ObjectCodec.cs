namespace Bassia.Core.Model;

using System.Formats.Cbor;

/// <summary>
/// The canonical encoding of every structural object: deterministic CBOR, an array whose first item names the type and
/// whose second is the encoding's version, then the fields in a fixed order. Maps are written as arrays of pairs sorted
/// by key, so the same object always has the same bytes and therefore the same id.
/// </summary>
public static class ObjectCodec
{
	private const int Version = 1;

	public static byte[] Encode(StoredObject value)
	{
		var writer = new CborWriter(CborConformanceMode.Canonical);
		switch (value)
		{
			case Tree tree:
				Head(writer, "tree", 1);
				writer.WriteStartArray(tree.Entries.Count);
				foreach (var entry in tree.Entries)
				{
					writer.WriteStartArray(5);
					writer.WriteTextString(entry.Name);
					WriteLeafFields(writer, entry.Leaf);
					writer.WriteEndArray();
				}

				writer.WriteEndArray();
				break;
			case FileObject file:
				Head(writer, "file", 2);
				writer.WriteInt64(file.Size);
				writer.WriteStartArray(file.Chunks.Count);
				foreach (var chunk in file.Chunks)
				{
					writer.WriteStartArray(2);
					WriteId(writer, chunk.Id);
					writer.WriteInt64(chunk.Length);
					writer.WriteEndArray();
				}

				writer.WriteEndArray();
				break;
			case ConflictObject conflict:
				Head(writer, "conflict", 2);
				WriteLeaves(writer, conflict.Bases);
				WriteLeaves(writer, conflict.Sides);
				break;
			case Commit commit:
				Head(writer, "commit", 8);
				WriteId(writer, commit.Tree);
				WriteIds(writer, commit.Parents);
				writer.WriteByteString(commit.ChangeId.ToBytes());
				writer.WriteStartArray(2);
				writer.WriteTextString(commit.Author.Agent);
				WriteText(writer, commit.Author.Run);
				writer.WriteEndArray();
				writer.WriteTextString(commit.Intent);
				WriteTime(writer, commit.Time);
				if (commit.Recipe is { } recipe)
				{
					writer.WriteStartArray(5);
					WriteText(writer, recipe.Prompt);
					WriteText(writer, recipe.Command);
					WriteText(writer, recipe.Agent);
					WriteText(writer, recipe.Model);
					WriteOptionalId(writer, recipe.TaskSpec);
					writer.WriteEndArray();
				}
				else
				{
					writer.WriteNull();
				}

				writer.WriteBoolean(commit.MachineMerged);
				break;
			case Operation operation:
				Head(writer, "op", 8);
				WriteIds(writer, operation.Parents);
				WriteId(writer, operation.View);
				writer.WriteTextString(operation.Description);
				WriteText(writer, operation.Workspace);
				WriteText(writer, operation.Agent);
				WriteText(writer, operation.Run);
				WriteTime(writer, operation.Time);
				writer.WriteInt64(operation.Seq);
				break;
			case View view:
				Head(writer, "view", 2);
				var refs = view.Refs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
				writer.WriteStartArray(refs.Count);
				foreach (var (name, id) in refs)
				{
					writer.WriteStartArray(2);
					writer.WriteTextString(name);
					WriteId(writer, id);
					writer.WriteEndArray();
				}

				writer.WriteEndArray();
				if (view.Workspace is { } workspace)
				{
					WriteWorkspace(writer, workspace);
				}
				else
				{
					writer.WriteNull();
				}

				break;
			default:
				throw new ArgumentException($"Cannot encode {value.GetType().Name}.", nameof(value));
		}

		writer.WriteEndArray();
		return writer.Encode();
	}

	/// <summary>The type an encoding names, without decoding the rest: "tree", "file", ...; null for a raw chunk.</summary>
	public static string? TypeOf(ReadOnlyMemory<byte> bytes)
	{
		try
		{
			var reader = new CborReader(bytes, CborConformanceMode.Canonical);
			if (reader.PeekState() != CborReaderState.StartArray)
			{
				return null;
			}

			reader.ReadStartArray();
			if (reader.PeekState() != CborReaderState.TextString)
			{
				return null;
			}

			var type = reader.ReadTextString();
			return type is "tree" or "file" or "conflict" or "commit" or "op" or "view" && reader.PeekState() == CborReaderState.UnsignedInteger
				&& reader.ReadInt32() == Version ? type : null;
		}
		catch (Exception ex) when (ex is CborContentException or InvalidOperationException or FormatException)
		{
			return null;
		}
	}

	public static T Decode<T>(ReadOnlyMemory<byte> bytes) where T : StoredObject =>
		Decode(bytes) as T ?? throw new InvalidDataException($"The object is not a {typeof(T).Name}.");

	/// <summary>Decodes a structural object; a malformed or non-canonical encoding is refused.</summary>
	public static StoredObject Decode(ReadOnlyMemory<byte> bytes)
	{
		try
		{
			var reader = new CborReader(bytes, CborConformanceMode.Canonical);
			reader.ReadStartArray();
			var type = reader.ReadTextString();
			var version = reader.ReadInt32();
			if (version != Version)
			{
				throw new InvalidDataException($"Unknown {type} encoding version {version}.");
			}

			StoredObject result = type switch
			{
				"tree" => ReadTree(reader),
				"file" => ReadFile(reader),
				"conflict" => new ConflictObject(ReadLeaves(reader), ReadLeaves(reader)),
				"commit" => ReadCommit(reader),
				"op" => ReadOperation(reader),
				"view" => ReadView(reader),
				_ => throw new InvalidDataException($"Unknown object type '{type}'.")
			};
			reader.ReadEndArray();
			if (reader.BytesRemaining != 0)
			{
				throw new InvalidDataException("Trailing bytes after the object.");
			}

			return result;
		}
		catch (Exception ex) when (ex is CborContentException or InvalidOperationException or FormatException or OverflowException)
		{
			throw new InvalidDataException($"Malformed object: {ex.Message}", ex);
		}
	}

	private static Tree ReadTree(CborReader reader)
	{
		var count = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite array.");
		var entries = new List<TreeEntry>(count);
		for (var index = 0; index < count; index++)
		{
			reader.ReadStartArray();
			var name = reader.ReadTextString();
			var leaf = ReadLeafFields(reader);
			reader.ReadEndArray();
			if (!PathNames.IsValidName(name))
			{
				throw new InvalidDataException($"'{name}' is not a valid entry name.");
			}

			if (entries.Count > 0 && string.CompareOrdinal(entries[^1].Name, name) >= 0)
			{
				throw new InvalidDataException("Tree entries are not sorted and unique.");
			}

			entries.Add(TreeEntry.Of(name, leaf));
		}

		reader.ReadEndArray();
		return new Tree(entries);
	}

	private static FileObject ReadFile(CborReader reader)
	{
		var size = reader.ReadInt64();
		var count = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite array.");
		var chunks = new List<ChunkRef>(count);
		for (var index = 0; index < count; index++)
		{
			reader.ReadStartArray();
			chunks.Add(new ChunkRef(ReadId(reader), reader.ReadInt64()));
			reader.ReadEndArray();
		}

		reader.ReadEndArray();
		if (chunks.Sum(chunk => chunk.Length) != size)
		{
			throw new InvalidDataException("A file's chunks do not add up to its size.");
		}

		return new FileObject(size, chunks);
	}

	private static Commit ReadCommit(CborReader reader)
	{
		var tree = ReadId(reader);
		var parents = ReadIds(reader);
		var change = ChangeId.FromBytes(reader.ReadByteString());
		reader.ReadStartArray();
		var author = new Author(reader.ReadTextString(), ReadText(reader));
		reader.ReadEndArray();
		var intent = reader.ReadTextString();
		var time = ReadTime(reader);
		ReplayRecipe? recipe = null;
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
		}
		else
		{
			reader.ReadStartArray();
			recipe = new ReplayRecipe(ReadText(reader), ReadText(reader), ReadText(reader), ReadText(reader), ReadOptionalId(reader));
			reader.ReadEndArray();
		}

		return new Commit(tree, parents, change, author, intent, time, recipe, reader.ReadBoolean());
	}

	private static Operation ReadOperation(CborReader reader) =>
		new(ReadIds(reader), ReadId(reader), reader.ReadTextString(), ReadText(reader), ReadText(reader), ReadText(reader), ReadTime(reader), reader.ReadInt64());

	private static View ReadView(CborReader reader)
	{
		var count = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite array.");
		var refs = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
		for (var index = 0; index < count; index++)
		{
			reader.ReadStartArray();
			refs[reader.ReadTextString()] = ReadId(reader);
			reader.ReadEndArray();
		}

		reader.ReadEndArray();
		WorkspaceRecord? workspace = null;
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
		}
		else
		{
			workspace = ReadWorkspace(reader);
		}

		return new View(refs, workspace);
	}

	private static void WriteWorkspace(CborWriter writer, WorkspaceRecord workspace)
	{
		writer.WriteStartArray(15);
		writer.WriteTextString(workspace.Id);
		writer.WriteTextString(workspace.Name);
		WriteText(writer, workspace.Parent);
		WriteId(writer, workspace.Base);
		writer.WriteInt64(workspace.BaseSeq);
		WriteId(writer, workspace.Working);
		writer.WriteTextString(workspace.Status);
		WriteTime(writer, workspace.Created);
		writer.WriteTextString(workspace.Owner);
		WriteText(writer, workspace.Path);
		WriteText(writer, workspace.Host);
		if (workspace.Run is { } run)
		{
			writer.WriteStartArray(13);
			writer.WriteTextString(run.RunId);
			writer.WriteTextString(run.Command);
			WriteText(writer, run.Prompt);
			writer.WriteTextString(run.Agent);
			WriteText(writer, run.Model);
			writer.WriteTextString(run.Status);
			WriteTime(writer, run.Started);
			WriteOptionalTime(writer, run.Finished);
			if (run.ExitCode is { } code)
			{
				writer.WriteInt32(code);
			}
			else
			{
				writer.WriteNull();
			}

			WriteOptionalId(writer, run.Log);
			WriteText(writer, run.Host);
			if (run.Pid is { } pid)
			{
				writer.WriteInt32(pid);
			}
			else
			{
				writer.WriteNull();
			}

			WriteText(writer, run.LastLine);
			writer.WriteEndArray();
		}
		else
		{
			writer.WriteNull();
		}

		if (workspace.LastLand is { } land)
		{
			writer.WriteStartArray(6);
			writer.WriteTextString(land.Outcome);
			WriteTime(writer, land.Time);
			if (land.Seq is { } seq)
			{
				writer.WriteInt64(seq);
			}
			else
			{
				writer.WriteNull();
			}

			WriteOptionalId(writer, land.Commit);
			writer.WriteStartArray(land.Conflicts.Count);
			foreach (var path in land.Conflicts)
			{
				writer.WriteTextString(path);
			}

			writer.WriteEndArray();
			writer.WriteTextString(land.Message);
			writer.WriteEndArray();
		}
		else
		{
			writer.WriteNull();
		}

		WriteText(writer, workspace.ReplayOf);
		WriteOptionalTime(writer, workspace.Closed);
		writer.WriteEndArray();
	}

	private static WorkspaceRecord ReadWorkspace(CborReader reader)
	{
		reader.ReadStartArray();
		var id = reader.ReadTextString();
		var name = reader.ReadTextString();
		var parent = ReadText(reader);
		var baseCommit = ReadId(reader);
		var baseSeq = reader.ReadInt64();
		var working = ReadId(reader);
		var status = reader.ReadTextString();
		var created = ReadTime(reader);
		var owner = reader.ReadTextString();
		var path = ReadText(reader);
		var host = ReadText(reader);
		RunRecord? run = null;
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
		}
		else
		{
			reader.ReadStartArray();
			var runId = reader.ReadTextString();
			var command = reader.ReadTextString();
			var prompt = ReadText(reader);
			var agent = reader.ReadTextString();
			var model = ReadText(reader);
			var runStatus = reader.ReadTextString();
			var started = ReadTime(reader);
			var finished = ReadOptionalTime(reader);
			int? exit = reader.PeekState() == CborReaderState.Null ? ReadNull<int>(reader) : reader.ReadInt32();
			var log = ReadOptionalId(reader);
			var runHost = ReadText(reader);
			int? pid = reader.PeekState() == CborReaderState.Null ? ReadNull<int>(reader) : reader.ReadInt32();
			var lastLine = ReadText(reader);
			reader.ReadEndArray();
			run = new RunRecord(runId, command, prompt, agent, model, runStatus, started, finished, exit, log, runHost, pid, lastLine);
		}

		LandRecord? land = null;
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
		}
		else
		{
			reader.ReadStartArray();
			var outcome = reader.ReadTextString();
			var time = ReadTime(reader);
			long? seq = reader.PeekState() == CborReaderState.Null ? ReadNull<long>(reader) : reader.ReadInt64();
			var commit = ReadOptionalId(reader);
			var count = reader.ReadStartArray() ?? 0;
			var conflicts = new List<string>(count);
			for (var index = 0; index < count; index++)
			{
				conflicts.Add(reader.ReadTextString());
			}

			reader.ReadEndArray();
			var message = reader.ReadTextString();
			reader.ReadEndArray();
			land = new LandRecord(outcome, time, seq, commit, conflicts, message);
		}

		var replayOf = ReadText(reader);
		var closed = ReadOptionalTime(reader);
		reader.ReadEndArray();
		return new WorkspaceRecord(id, name, parent, baseCommit, baseSeq, working, status, created, owner, path, host, run, land, replayOf, closed);
	}

	private static T? ReadNull<T>(CborReader reader) where T : struct
	{
		reader.ReadNull();
		return null;
	}

	private static void Head(CborWriter writer, string type, int fields)
	{
		writer.WriteStartArray(2 + fields);
		writer.WriteTextString(type);
		writer.WriteInt32(Version);
	}

	private static void WriteLeafFields(CborWriter writer, Leaf leaf)
	{
		writer.WriteInt32((int)leaf.Kind);
		WriteId(writer, leaf.Id);
		writer.WriteInt64(leaf.Size);
		writer.WriteBoolean(leaf.Executable);
	}

	private static Leaf ReadLeafFields(CborReader reader)
	{
		var kind = reader.ReadInt32();
		if (kind is < 0 or > (int)EntryKind.Conflict)
		{
			throw new InvalidDataException($"Unknown entry kind {kind}.");
		}

		var id = ReadId(reader);
		var size = reader.ReadInt64();
		if (size < 0)
		{
			throw new InvalidDataException("A negative size.");
		}

		return new Leaf((EntryKind)kind, id, size, reader.ReadBoolean());
	}

	private static void WriteLeaves(CborWriter writer, IReadOnlyList<Leaf?> leaves)
	{
		writer.WriteStartArray(leaves.Count);
		foreach (var leaf in leaves)
		{
			if (leaf is null)
			{
				writer.WriteNull();
			}
			else
			{
				writer.WriteStartArray(4);
				WriteLeafFields(writer, leaf);
				writer.WriteEndArray();
			}
		}

		writer.WriteEndArray();
	}

	private static IReadOnlyList<Leaf?> ReadLeaves(CborReader reader)
	{
		var count = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite array.");
		var leaves = new List<Leaf?>(count);
		for (var index = 0; index < count; index++)
		{
			if (reader.PeekState() == CborReaderState.Null)
			{
				reader.ReadNull();
				leaves.Add(null);
			}
			else
			{
				reader.ReadStartArray();
				leaves.Add(ReadLeafFields(reader));
				reader.ReadEndArray();
			}
		}

		reader.ReadEndArray();
		return leaves;
	}

	private static void WriteId(CborWriter writer, ObjectId id) => writer.WriteByteString(id.ToBytes());

	private static ObjectId ReadId(CborReader reader) => ObjectId.FromBytes(reader.ReadByteString());

	private static void WriteOptionalId(CborWriter writer, ObjectId? id)
	{
		if (id is { } value)
		{
			WriteId(writer, value);
		}
		else
		{
			writer.WriteNull();
		}
	}

	private static ObjectId? ReadOptionalId(CborReader reader)
	{
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
			return null;
		}

		return ReadId(reader);
	}

	private static void WriteIds(CborWriter writer, IReadOnlyList<ObjectId> ids)
	{
		writer.WriteStartArray(ids.Count);
		foreach (var id in ids)
		{
			WriteId(writer, id);
		}

		writer.WriteEndArray();
	}

	private static IReadOnlyList<ObjectId> ReadIds(CborReader reader)
	{
		var count = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite array.");
		var ids = new List<ObjectId>(count);
		for (var index = 0; index < count; index++)
		{
			ids.Add(ReadId(reader));
		}

		reader.ReadEndArray();
		return ids;
	}

	private static void WriteText(CborWriter writer, string? text)
	{
		if (text is null)
		{
			writer.WriteNull();
		}
		else
		{
			writer.WriteTextString(text);
		}
	}

	private static string? ReadText(CborReader reader)
	{
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
			return null;
		}

		return reader.ReadTextString();
	}

	private static void WriteTime(CborWriter writer, DateTimeOffset time) => writer.WriteInt64(time.ToUnixTimeMilliseconds());

	private static DateTimeOffset ReadTime(CborReader reader) => DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());

	private static void WriteOptionalTime(CborWriter writer, DateTimeOffset? time)
	{
		if (time is { } value)
		{
			WriteTime(writer, value);
		}
		else
		{
			writer.WriteNull();
		}
	}

	private static DateTimeOffset? ReadOptionalTime(CborReader reader)
	{
		if (reader.PeekState() == CborReaderState.Null)
		{
			reader.ReadNull();
			return null;
		}

		return ReadTime(reader);
	}
}
