namespace Bassia.Core.Model;

using System.Numerics;

/// <summary>Content-defined chunking parameters: the smallest, average and largest chunk.</summary>
public sealed record ChunkParameters(int Min, int Average, int Max)
{
	/// <summary>The design's proposal: 256 KiB, 1 MiB, 4 MiB.</summary>
	public static readonly ChunkParameters Default = new(256 * 1024, 1024 * 1024, 4 * 1024 * 1024);

	public void Validate()
	{
		if (Min < 64 || Average <= Min || Max <= Average || !BitOperations.IsPow2(Average))
		{
			throw new ArgumentException("Chunk parameters need 64 <= min < average < max, with a power-of-two average.");
		}
	}
}

/// <summary>
/// FastCDC (Xia et al., 2016/2020) with normalized chunking: cut points depend only on content, so an edit moves only
/// the chunks around it and a rewrite that keeps part of a file uploads only the new part. The gear table is derived
/// from a fixed seed and must never change: it decides every chunk id ever stored.
/// </summary>
public sealed class Chunker
{
	private static readonly ulong[] Gear = BuildGear();

	private readonly ChunkParameters parameters;
	private readonly ulong maskSmall;
	private readonly ulong maskLarge;

	public Chunker(ChunkParameters? parameters = null)
	{
		this.parameters = parameters ?? ChunkParameters.Default;
		this.parameters.Validate();
		var bits = BitOperations.Log2((uint)this.parameters.Average);
		maskSmall = SpreadMask(bits + 2);
		maskLarge = SpreadMask(bits - 2);
	}

	public ChunkParameters Parameters => parameters;

	/// <summary>The length of the chunk that starts at the beginning of <paramref name="data"/>.</summary>
	public int Cut(ReadOnlySpan<byte> data)
	{
		var length = data.Length;
		if (length <= parameters.Min)
		{
			return length;
		}

		if (length > parameters.Max)
		{
			length = parameters.Max;
		}

		var normal = Math.Min(parameters.Average, length);
		ulong hash = 0;
		var index = parameters.Min;
		for (; index < normal; index++)
		{
			hash = (hash << 1) + Gear[data[index]];
			if ((hash & maskSmall) == 0)
			{
				return index + 1;
			}
		}

		for (; index < length; index++)
		{
			hash = (hash << 1) + Gear[data[index]];
			if ((hash & maskLarge) == 0)
			{
				return index + 1;
			}
		}

		return length;
	}

	/// <summary>Every chunk of a whole buffer, as (offset, length).</summary>
	public IEnumerable<(int Offset, int Length)> Split(ReadOnlyMemory<byte> data)
	{
		var offset = 0;
		while (offset < data.Length)
		{
			var length = Cut(data.Span[offset..]);
			yield return (offset, length);
			offset += length;
		}
	}

	/// <summary>
	/// Chunks a stream without holding more than one maximum chunk (plus one read) in memory, calling
	/// <paramref name="onChunk"/> with each chunk's bytes.
	/// </summary>
	public async Task SplitAsync(Stream stream, Func<ReadOnlyMemory<byte>, Task> onChunk, CancellationToken ct = default)
	{
		var buffer = new byte[parameters.Max * 2];
		var filled = 0;
		var end = false;
		while (true)
		{
			while (!end && filled < buffer.Length)
			{
				var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
				if (read == 0)
				{
					end = true;
				}

				filled += read;
			}

			if (filled == 0)
			{
				return;
			}

			var start = 0;
			while (filled - start >= parameters.Max || (end && start < filled))
			{
				var length = Cut(buffer.AsSpan(start, filled - start));
				await onChunk(buffer.AsMemory(start, length));
				start += length;
			}

			Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
			filled -= start;
			if (end && filled == 0)
			{
				return;
			}
		}
	}

	// A mask with the given number of one bits spread over the upper 48 bits, as FastCDC recommends.
	private static ulong SpreadMask(int bits)
	{
		bits = Math.Clamp(bits, 1, 48);
		ulong mask = 0;
		var step = 48.0 / bits;
		for (var index = 0; index < bits; index++)
		{
			mask |= 1UL << (63 - (int)(index * step));
		}

		return mask;
	}

	private static ulong[] BuildGear()
	{
		// splitmix64 from a fixed seed: deterministic forever.
		var table = new ulong[256];
		var state = 0x6261737369614344UL; // "bassiaCD"
		for (var index = 0; index < table.Length; index++)
		{
			state += 0x9E3779B97F4A7C15UL;
			var z = state;
			z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
			z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
			table[index] = z ^ (z >> 31);
		}

		return table;
	}
}
