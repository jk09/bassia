namespace Bassia.Stores;

using System.Text.Json;
using System.Text.Json.Serialization;
using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>
/// The wire format of the client-server protocol: JSON with snake_case names for calls, raw bytes for objects. Ids are
/// hex strings. The same records are used by <c>bassia-server</c> and by the remote stores of a clone.
/// </summary>
public static class Api
{
	public const string Prefix = "/api/v1";

	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new ObjectIdConverter(), new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
	};

	public sealed class ObjectIdConverter : JsonConverter<ObjectId>
	{
		public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ObjectId.Parse(reader.GetString()!);

		public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Hex);
	}

	public sealed record Info(string Server, string Version, int ChunkMin, int ChunkAverage, int ChunkMax, string Storage);

	public sealed record HeadReply(ObjectId? Head);

	public sealed record HeadEntry(string Name, ObjectId Head);

	public sealed record AdvanceRequest(string Name, ObjectId? Expected, ObjectId Next);

	public sealed record AdvanceReply(bool Success, ObjectId? Current);

	public sealed record DeleteHeadRequest(string Name, ObjectId Expected);

	public sealed record IdsRequest(IReadOnlyList<ObjectId> Ids);

	public sealed record HaveReply(IReadOnlyList<bool> Have);

	public sealed record StatReply(long Length);

	public sealed record UploadRequest(IReadOnlyList<ObjectId> Ids, IReadOnlyList<long> Lengths);

	public sealed record UrlEntry(ObjectId Id, string? Url, DateTimeOffset Expires);

	public sealed record UrlsReply(IReadOnlyList<UrlEntry> Urls);

	public sealed record BatchRequest(IReadOnlyList<ObjectId> Ids, int Depth = 0);

	public sealed record BatchObject(ObjectId Id, string Data);

	public sealed record BatchReply(IReadOnlyList<BatchObject> Objects);

	/// <summary>The current mainline head, its seq and commit (GetHead).</summary>
	public sealed record MainReply(ObjectId Head, long Seq, ObjectId Commit, ObjectId Tree);

	public sealed record LeafDto(EntryKind Kind, ObjectId Id, long Size, bool Executable)
	{
		public Leaf ToLeaf() => new(Kind, Id, Size, Executable);

		public static LeafDto? From(Leaf? leaf) => leaf is null ? null : new LeafDto(leaf.Kind, leaf.Id, leaf.Size, leaf.Executable);
	}

	public sealed record VersionDto(string Path, long Seq, LeafDto? Leaf)
	{
		public PathVersion ToVersion() => new(Path, Seq, Leaf?.ToLeaf());

		public static VersionDto From(PathVersion version) => new(version.Path, version.Seq, LeafDto.From(version.Leaf));
	}

	public sealed record ListingDto(string Path, bool IsDirectory, VersionDto? Version);

	public sealed record StatPathsRequest(IReadOnlyList<string> Paths, long Seq);

	public sealed record StatPathsReply(IReadOnlyList<VersionDto?> Versions);

	public sealed record ApplyRequest(long Seq, IReadOnlyList<ChangeDto> Changes);

	public sealed record ChangeDto(string Path, LeafDto? Leaf);

	public sealed record SeqReply(long Seq);

	public sealed record AcquireRequest(string Prefix, LeaseMode Mode, string Owner, double TtlSeconds, double WaitSeconds, string? Run);

	public sealed record LeaseReply(Lease? Lease, IReadOnlyList<Lease> Blocking);

	public sealed record RenewRequest(string Id, long Token, double TtlSeconds);

	public sealed record ReleaseRequest(string Id, long Token);

	public sealed record LandRequestDto(string Workspace, bool Strict, IReadOnlyList<string>? ReadSet, IReadOnlyList<LeaseToken>? Leases, bool Keep, bool DryRun, string? Agent)
	{
		public LandRequest ToRequest() => new(Workspace, Strict, ReadSet, Leases, Keep, DryRun, Agent);

		public static LandRequestDto From(LandRequest request) =>
			new(request.Workspace, request.Strict, request.ReadSet, request.Leases, request.Keep, request.DryRun, request.Agent);
	}

	/// <summary>A change pushed to watchers: a landing (with the prefixes it touched) or a workspace that moved.</summary>
	public sealed record WatchEvent(string Kind, long? Seq = null, IReadOnlyList<string>? Prefixes = null, string? Workspace = null, DateTimeOffset? Time = null);

	public sealed record ErrorReply(string Error);
}
