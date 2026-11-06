namespace Bassia.Stores;

using System.Net;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Bassia.Core.Model;
using Bassia.Core.Storage;

/// <summary>
/// The object store on Azure Blob Storage: one block blob per object, keyed by hash prefix under
/// <paramref name="prefix"/> (<c>objects/ab/cdef...</c>) in one container. Blob Storage gives strong read-after-write
/// consistency, which is all the contract asks. Large chunks move between clients and the container directly on SAS
/// URLs - signed with the account key, or with a user delegation key under Microsoft Entra ID - so the server never
/// proxies them.
/// </summary>
public sealed class AzureBlobObjectStore : IObjectStore, IObjectStoreMaintenance
{
	private readonly BlobContainerClient container;
	private readonly string prefix;
	private readonly BlobServiceClient? service;
	private UserDelegationKey? delegationKey;
	private readonly SemaphoreSlim keyGate = new(1, 1);

	public AzureBlobObjectStore(BlobContainerClient container, string prefix = "objects/", BlobServiceClient? service = null)
	{
		this.container = container;
		this.prefix = prefix;
		this.service = service;
	}

	/// <summary>A store from an account connection string (account key, or Azurite's development string).</summary>
	public static AzureBlobObjectStore FromConnectionString(string connectionString, string containerName, string prefix = "objects/")
	{
		var service = new BlobServiceClient(connectionString);
		return new AzureBlobObjectStore(service.GetBlobContainerClient(containerName), prefix, service);
	}

	/// <summary>A store from a container URL, authenticated with Microsoft Entra ID (managed identity, workload identity, Azure CLI, ...).</summary>
	public static AzureBlobObjectStore FromContainerUrl(Uri containerUrl, string prefix = "objects/")
	{
		var credential = new DefaultAzureCredential();
		var container = new BlobContainerClient(containerUrl, credential);
		var service = new BlobServiceClient(new Uri(containerUrl.GetLeftPart(UriPartial.Authority)), credential);
		return new AzureBlobObjectStore(container, prefix, service);
	}

	public TimeSpan UrlLifetime { get; init; } = TimeSpan.FromMinutes(30);

	public async Task CreateIfMissingAsync(CancellationToken ct = default) => await container.CreateIfNotExistsAsync(cancellationToken: ct);

	private string Key(ObjectId id) => $"{prefix}{id.Hex[..2]}/{id.Hex[2..]}";

	private BlockBlobClient Blob(ObjectId id) => container.GetBlockBlobClient(Key(id));

	public async ValueTask<IReadOnlyList<bool>> HasAsync(IReadOnlyList<ObjectId> ids, CancellationToken ct = default)
	{
		var results = new bool[ids.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, ids.Count), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
			async (index, token) => results[index] = (await Blob(ids[index]).ExistsAsync(token)).Value);
		return results;
	}

	public async ValueTask<ObjectStat?> StatAsync(ObjectId id, CancellationToken ct = default)
	{
		try
		{
			var properties = await Blob(id).GetPropertiesAsync(cancellationToken: ct);
			return new ObjectStat(id, properties.Value.ContentLength);
		}
		catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(ObjectId id, Core.Storage.ByteRange? range = null, CancellationToken ct = default)
	{
		try
		{
			var options = new BlobDownloadOptions();
			if (range is { Length: > 0 } r)
			{
				options.Range = new HttpRange(r.Offset, r.Length);
			}

			var result = await Blob(id).DownloadContentAsync(options, ct);
			return result.Value.Content.ToMemory();
		}
		catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
		{
			return null;
		}
		catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.RequestedRangeNotSatisfiable)
		{
			return ReadOnlyMemory<byte>.Empty;
		}
	}

	public async ValueTask PutAsync(ObjectId id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
	{
		try
		{
			// Objects never change: write only where none is, and treat "already there" as done.
			await container.GetBlobClient(Key(id)).UploadAsync(new BinaryData(bytes), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
		}
		catch (RequestFailedException ex) when (ex.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.PreconditionFailed)
		{
		}
	}

	public async ValueTask<UploadTicket> BeginUploadAsync(ObjectId id, long length, CancellationToken ct = default)
	{
		var expires = DateTimeOffset.UtcNow + UrlLifetime;
		return new UploadTicket(id, await SasAsync(id, BlobSasPermissions.Create | BlobSasPermissions.Write, expires, ct), expires);
	}

	/// <summary>A SAS URL to read one object directly from the container.</summary>
	public async Task<Uri> ReadUrlAsync(ObjectId id, CancellationToken ct = default) =>
		await SasAsync(id, BlobSasPermissions.Read, DateTimeOffset.UtcNow + UrlLifetime, ct);

	private async Task<Uri> SasAsync(ObjectId id, BlobSasPermissions permissions, DateTimeOffset expires, CancellationToken ct)
	{
		var blob = Blob(id);
		var builder = new BlobSasBuilder(permissions, expires)
		{
			BlobContainerName = container.Name,
			BlobName = Key(id),
			Resource = "b",
			StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5)
		};
		if (blob.CanGenerateSasUri)
		{
			return blob.GenerateSasUri(builder);
		}

		// Microsoft Entra ID: sign with a user delegation key, refreshed before it runs out.
		var key = await DelegationKeyAsync(expires, ct);
		var sas = builder.ToSasQueryParameters(key, container.AccountName);
		return new BlobUriBuilder(blob.Uri) { Sas = sas }.ToUri();
	}

	private async Task<UserDelegationKey> DelegationKeyAsync(DateTimeOffset until, CancellationToken ct)
	{
		await keyGate.WaitAsync(ct);
		try
		{
			if (delegationKey is null || delegationKey.SignedExpiresOn < until)
			{
				var client = service ?? throw new VersionException("Signing SAS URLs needs the account key or a blob service client.");
				delegationKey = (await client.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-5), until + TimeSpan.FromHours(6), ct)).Value;
			}

			return delegationKey;
		}
		finally
		{
			keyGate.Release();
		}
	}

	public async IAsyncEnumerable<Core.Storage.BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
		{
			var hex = item.Name[prefix.Length..].Replace("/", "");
			if (ObjectId.TryParse(hex, out var id))
			{
				yield return new Core.Storage.BlobInfo(id, item.Properties.ContentLength ?? 0, item.Properties.LastModified ?? DateTimeOffset.UtcNow);
			}
		}
	}

	public async ValueTask DeleteAsync(ObjectId id, CancellationToken ct = default) =>
		await Blob(id).DeleteIfExistsAsync(cancellationToken: ct);
}
