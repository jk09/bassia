namespace Bassia.Tests.Core;

using System.Reflection;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

public class ModelTests
{
	private static readonly ObjectId A = ObjectId.Of([1]);
	private static readonly ObjectId B = ObjectId.Of([2]);

	[Fact]
	public void ObjectIds_AreBlake3OfTheBytes()
	{
		Assert.Equal("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85", ObjectId.Of("abc"u8).Hex);
		Assert.Equal(ObjectId.Of("abc"u8), ObjectId.Parse(ObjectId.Of("abc"u8).Hex.ToUpperInvariant()));
		Assert.False(ObjectId.TryParse("xyz", out _));
	}

	public static TheoryData<StoredObject> Objects => new()
	{
		Tree.Of([new TreeEntry("b.txt", EntryKind.File, A, 3), new TreeEntry("a", EntryKind.Tree, B, 10, false), new TreeEntry("run.sh", EntryKind.Chunked, A, 9000, true)]),
		new FileObject(7, [new ChunkRef(A, 3), new ChunkRef(B, 4)]),
		new ConflictObject([new Leaf(EntryKind.File, A, 1)], [null, new Leaf(EntryKind.File, B, 2)]),
		new Commit(A, [B], ChangeId.New(), new Author("claude", "run-1"), "add a feature\n\nbody", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
			new ReplayRecipe("prompt", null, "claude", "opus", B), true),
		new Commit(A, [], ChangeId.New(), new Author("me"), "", DateTimeOffset.FromUnixTimeMilliseconds(5)),
		new Operation([A], B, "land", "w1", "claude", null, DateTimeOffset.FromUnixTimeMilliseconds(9), 42),
		new View(new Dictionary<string, ObjectId> { ["main"] = A, ["base"] = B }),
		new View(new Dictionary<string, ObjectId>(), new WorkspaceRecord("w1", "feature", "w0", A, 3, B, WorkspaceStatus.NeedsReplay,
			DateTimeOffset.FromUnixTimeMilliseconds(1), "claude", "/tmp/w", "host",
			new RunRecord("run-x", "claude -p", "do it", "claude", "opus", RunStatus.Failed, DateTimeOffset.FromUnixTimeMilliseconds(2),
				DateTimeOffset.FromUnixTimeMilliseconds(3), 1, A, "host", 77, "last"),
			new LandRecord(LandOutcome.NeedsReplay, DateTimeOffset.FromUnixTimeMilliseconds(4), null, null, ["a/b"], "moved"), "w-1",
			DateTimeOffset.FromUnixTimeMilliseconds(8)))
	};

	[Theory]
	[MemberData(nameof(Objects))]
	public void Encoding_RoundTripsAndIsDeterministic(StoredObject value)
	{
		var bytes = ObjectCodec.Encode(value);
		var decoded = ObjectCodec.Decode(bytes);
		Assert.Equal(bytes, ObjectCodec.Encode(decoded));
		Assert.NotNull(ObjectCodec.TypeOf(bytes));
		Assert.Equal(ObjectCodec.Encode(value), bytes);
	}

	[Fact]
	public void Decoding_RefusesUnsortedTreesAndGarbage()
	{
		Assert.Throws<InvalidDataException>(() => ObjectCodec.Decode(new byte[] { 1, 2, 3 }));
		Assert.Null(ObjectCodec.TypeOf(new byte[] { 0x61, 0x62 }));
		var unsorted = new Tree([new TreeEntry("b", EntryKind.File, A, 1), new TreeEntry("a", EntryKind.File, A, 1)]);
		Assert.Throws<InvalidDataException>(() => ObjectCodec.Decode(ObjectCodec.Encode(unsorted)));
		Assert.Throws<InvalidDataException>(() => Tree.Of([new TreeEntry("a", EntryKind.File, A, 1), new TreeEntry("a", EntryKind.File, B, 1)]));
	}

	[Fact]
	public void Chunker_CutsByContentSoAnEditMovesOnlyNearbyChunks()
	{
		var chunker = new Chunker(TestRepo.SmallChunks);
		var data = new byte[64 * 1024];
		new Random(7).NextBytes(data);
		var chunks = chunker.Split(data).ToList();
		Assert.Equal(data.Length, chunks.Sum(chunk => chunk.Length));
		Assert.All(chunks.SkipLast(1), chunk => Assert.InRange(chunk.Length, 64, 1024));
		Assert.InRange(chunks.Count, 64 * 1024 / 1024, 64 * 1024 / 64);

		// Insert a byte in the middle: most chunks keep their ids.
		var edited = data.Take(32 * 1024).Append((byte)42).Concat(data.Skip(32 * 1024)).ToArray();
		var before = chunks.Select(chunk => ObjectId.Of(data.AsSpan(chunk.Offset, chunk.Length))).ToHashSet();
		var after = chunker.Split(edited).Select(chunk => ObjectId.Of(edited.AsSpan(chunk.Offset, chunk.Length))).ToList();
		Assert.True(after.Count(before.Contains) >= after.Count - 3, "an insertion re-cuts only the chunks around it");
	}

	[Fact]
	public async Task Chunker_StreamingCutsTheSameAsWholeBuffer()
	{
		var chunker = new Chunker(TestRepo.SmallChunks);
		var data = new byte[20_000];
		new Random(3).NextBytes(data);
		var expected = chunker.Split(data).Select(chunk => chunk.Length).ToList();
		var streamed = new List<int>();
		await chunker.SplitAsync(new MemoryStream(data), chunk => { streamed.Add(chunk.Length); return Task.CompletedTask; });
		Assert.Equal(expected, streamed);
	}

	[Fact]
	public async Task Files_SmallAreOneChunkLargeAreChunkedAndIdentifyMatchesWrite()
	{
		var repo = new TestRepo();
		var small = await repo.Repo.Objects.WriteContentAsync(new byte[10]);
		Assert.Equal(EntryKind.File, small.Kind);
		var data = new byte[10_000];
		new Random(1).NextBytes(data);
		var large = await repo.Repo.Objects.WriteContentAsync(data);
		Assert.Equal(EntryKind.Chunked, large.Kind);
		Assert.Equal(large, repo.Repo.Objects.Identify(data));
		Assert.Equal(data, await repo.Repo.Objects.ReadContentAsync(large));
		Assert.Equal(data.AsSpan(5000, 300).ToArray(), await repo.Repo.Objects.ReadRangeAsync(large, 5000, 300));
	}

	[Fact]
	public async Task Uploads_SkipChunksTheStoreAlreadyHas()
	{
		var counting = new CountingObjectStore(new MemoryObjectStore());
		var repo = new TestRepo(counting);
		var data = new byte[10_000];
		new Random(1).NextBytes(data);
		await repo.Repo.Objects.WriteContentAsync(data);
		var first = counting.Puts;
		data[9_990] ^= 0xFF;
		await repo.Repo.Objects.WriteContentAsync(data);
		Assert.InRange(counting.Puts - first, 1, 3);
	}

	[Theory]
	[InlineData("*.md", "docs/a.md", true)]
	[InlineData("*.md", "a.mdx", false)]
	[InlineData("bin/", "src/app/bin/x.dll", true)]
	[InlineData("bin/", "src/binary.c", false)]
	[InlineData("docs/**", "docs/a/b.txt", true)]
	[InlineData("docs/**", "src/docs/a.txt", false)]
	[InlineData("src/*/gen/", "src/app/gen/x.cs", true)]
	[InlineData("node_modules", "web/node_modules/x/y.js", true)]
	public void Globs_MatchLikeGitignore(string pattern, string path, bool expected) =>
		Assert.Equal(expected, new PathGlob(pattern).IsMatch(path));

	[Fact]
	public void TheCore_ReferencesNoHttpCloudOrDatabaseLibrary()
	{
		var references = typeof(Repository).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToList();
		Assert.DoesNotContain(references, name => name.StartsWith("AWSSDK", StringComparison.Ordinal) || name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Npgsql", StringComparison.Ordinal) || name == "System.Net.Http" || name.StartsWith("Azure", StringComparison.Ordinal)
			|| name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || name == "System.Data.Common");
		Assert.DoesNotContain(typeof(Repository).Assembly.GetTypes(), type => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
			.Any(field => field.FieldType.FullName == "System.Net.Http.HttpClient"));
	}
}
