namespace Bassia.Stores;

using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// The object store on Amazon S3: one object per id, keyed by hash prefix under <paramref name="prefix"/>
/// (<c>objects/ab/cdef...</c>). S3 gives read-after-write per key, which is all the contract asks. Large chunks move
/// between clients and the bucket on presigned URLs; the server never proxies them.
/// </summary>
public sealed class S3ObjectStore(IAmazonS3 client, string bucket, string prefix = "objects/") : IObjectStore, IObjectStoreMaintenance
{
	public TimeSpan UrlLifetime { get; init; } = TimeSpan.FromMinutes(30);

	private string Key(ObjectId id) => $"{prefix}{id.Hex[..2]}/{id.Hex[2..]}";

	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var results = await Task.WhenAll(ids.Select(async id => await StatAsync(id, ct) is not null));
		return results;
	}

	public async ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default)
	{
		try
		{
			var metadata = await client.GetObjectMetadataAsync(bucket, Key(id), ct);
			return new ObjectStat(id, metadata.ContentLength);
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, Core.Storage.ByteRange? range = null, CancellationToken ct = default)
	{
		var request = new GetObjectRequest { BucketName = bucket, Key = Key(id) };
		if (range is { Length: > 0 } r)
		{
			request.ByteRange = new Amazon.S3.Model.ByteRange(r.Offset, r.Offset + r.Length - 1);
		}

		try
		{
			using var response = await client.GetObjectAsync(request, ct);
			using var buffer = new MemoryStream();
			await response.ResponseStream.CopyToAsync(buffer, ct);
			return buffer.ToArray();
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		using var stream = new MemoryStream(bytes.ToArray(), false);
		await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key(id), InputStream = stream }, ct);
	}

	public async ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default)
	{
		var expires = DateTime.UtcNow + UrlLifetime;
		var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = Key(id), Verb = HttpVerb.PUT, Expires = expires });
		return new UploadTicket(id, new Uri(url), expires);
	}

	/// <summary>A presigned read URL, so a client fetches a chunk from the bucket directly.</summary>
	public async Task<Uri> ReadUrlAsync(ObjectId id) =>
		new(await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = Key(id), Verb = HttpVerb.GET, Expires = DateTime.UtcNow + UrlLifetime }));

	public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
		ListObjectsV2Response response;
		do
		{
			response = await client.ListObjectsV2Async(request, ct);
			foreach (var item in response.S3Objects ?? [])
			{
				var hex = item.Key[prefix.Length..].Replace("/", "");
				if (ObjectId.TryParse(hex, out var id))
				{
					yield return new BlobInfo(id, item.Size ?? 0, item.LastModified ?? DateTime.UtcNow);
				}
			}

			request.ContinuationToken = response.NextContinuationToken;
		}
		while (response.IsTruncated == true);
	}

	public async ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default) =>
		await client.DeleteObjectAsync(bucket, Key(id), ct);
}
