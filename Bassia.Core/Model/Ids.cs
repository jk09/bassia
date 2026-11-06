namespace Bassia.Core.Model;

using System.Security.Cryptography;

/// <summary>
/// The id of a stored object: the BLAKE3-256 hash of its stored bytes (a chunk's raw bytes, or a structural object's
/// canonical CBOR encoding), written as 64 lowercase hex digits. Equal ids mean equal content everywhere, so uploads
/// deduplicate and equal subtrees are skipped by comparison alone.
/// </summary>
public readonly record struct ObjectId
{
	public const int Bytes = 32;

	private ObjectId(string hex) => Hex = hex;

	public string Hex { get; }

	public static ObjectId Of(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(Blake3.Hasher.Hash(bytes).AsSpan()));

	public static ObjectId FromBytes(ReadOnlySpan<byte> bytes) =>
		bytes.Length == Bytes ? new(Convert.ToHexStringLower(bytes)) : throw new FormatException($"An object id has {Bytes} bytes, not {bytes.Length}.");

	public static ObjectId Parse(string hex) =>
		TryParse(hex, out var id) ? id : throw new FormatException($"'{hex}' is not an object id (64 hex digits).");

	public static bool TryParse(string? hex, out ObjectId id)
	{
		id = default;
		if (hex is null || hex.Length != Bytes * 2 || !hex.All(Uri.IsHexDigit))
		{
			return false;
		}

		id = new ObjectId(hex.ToLowerInvariant());
		return true;
	}

	public byte[] ToBytes() => Convert.FromHexString(Hex);

	public bool IsEmpty => Hex is null;

	/// <summary>The first 12 hex digits, as listings show an id.</summary>
	public string Short => Hex[..12];

	public override string ToString() => Hex ?? "";
}

/// <summary>
/// The stable identity of a unit of work: a random 128-bit value chosen when a workspace's change is created. Unlike the
/// CommitId it survives snapshots, rebase, landing and replay, so agents and reviewers keep following the same change.
/// </summary>
public readonly record struct ChangeId
{
	public const int Bytes = 16;

	private ChangeId(string hex) => Hex = hex;

	public string Hex { get; }

	public static ChangeId New() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Bytes)));

	public static ChangeId FromBytes(ReadOnlySpan<byte> bytes) =>
		bytes.Length == Bytes ? new(Convert.ToHexStringLower(bytes)) : throw new FormatException($"A change id has {Bytes} bytes, not {bytes.Length}.");

	public static ChangeId Parse(string hex) =>
		hex.Length == Bytes * 2 && hex.All(Uri.IsHexDigit) ? new(hex.ToLowerInvariant()) : throw new FormatException($"'{hex}' is not a change id (32 hex digits).");

	public byte[] ToBytes() => Convert.FromHexString(Hex);

	public string Short => Hex[..12];

	public override string ToString() => Hex ?? "";
}
