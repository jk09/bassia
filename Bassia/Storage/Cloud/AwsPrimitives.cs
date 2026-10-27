namespace Bassia.Storage.Cloud;

using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

/// <summary>
/// Where the cloud storage lives in AWS: an S3 bucket (and key prefix) for packs, a DynamoDB table for records, the region,
/// and - for emulators such as MinIO and DynamoDB Local - the endpoints to use instead of AWS's.
/// </summary>
internal sealed record AwsCloudOptions(string Bucket, string Table, string? Region = null, string Prefix = "", string? S3ServiceUrl = null, string? DynamoServiceUrl = null)
{
	public IAmazonS3 S3Client() => S3ServiceUrl is null
		? Region is null ? new AmazonS3Client() : new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(Region))
		: new AmazonS3Client(new AmazonS3Config { ServiceURL = S3ServiceUrl, ForcePathStyle = true, AuthenticationRegion = Region ?? "us-east-1" });

	public IAmazonDynamoDB DynamoClient() => DynamoServiceUrl is null
		? Region is null ? new AmazonDynamoDBClient() : new AmazonDynamoDBClient(Amazon.RegionEndpoint.GetBySystemName(Region))
		: new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = DynamoServiceUrl, AuthenticationRegion = Region ?? "us-east-1" });

	/// <summary>
	/// What the deployment must have for the history to be recoverable: versioning on the bucket and point-in-time
	/// recovery on the table. Empty when both are on.
	/// </summary>
	public async Task<IReadOnlyList<string>> CheckAsync()
	{
		var problems = new List<string>();
		var versioning = await S3Client().GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = Bucket });
		if (versioning.VersioningConfig?.Status != VersionStatus.Enabled)
		{
			problems.Add($"S3 bucket '{Bucket}' must have versioning enabled, so deleted or overwritten packs can be restored.");
		}

		var backups = await DynamoClient().DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = Table });
		if (backups.ContinuousBackupsDescription?.PointInTimeRecoveryDescription?.PointInTimeRecoveryStatus != PointInTimeRecoveryStatus.ENABLED)
		{
			problems.Add($"DynamoDB table '{Table}' must have point-in-time recovery enabled, so refs can be restored to the same moment as the packs.");
		}

		return problems;
	}
}

/// <summary><see cref="IObjectStore"/> on S3: large packs go up and down in parts (the SDK's transfer utility).</summary>
internal sealed class S3ObjectStore(IAmazonS3 client, string bucket, string prefix = "") : IObjectStore
{
	private readonly TransferUtility transfer = new(client);

	public string Location => $"s3://{bucket}/{prefix}";

	private string KeyOf(string key) => prefix + key;

	public Task PutFileAsync(string key, string path) => transfer.UploadAsync(path, bucket, KeyOf(key));

	public async Task GetFileAsync(string key, string path)
	{
		try
		{
			await transfer.DownloadAsync(new TransferUtilityDownloadRequest { BucketName = bucket, Key = KeyOf(key), FilePath = path });
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
		{
			throw new StorageException($"No object '{key}' in {Location}.");
		}
	}

	public Task CopyAsync(string fromKey, string toKey) =>
		client.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = KeyOf(fromKey), DestinationBucket = bucket, DestinationKey = KeyOf(toKey) });

	public async Task<IReadOnlyList<string>> ListAsync(string keyPrefix)
	{
		var keys = new List<string>();
		var request = new ListObjectsV2Request { BucketName = bucket, Prefix = KeyOf(keyPrefix) };
		ListObjectsV2Response response;
		do
		{
			response = await client.ListObjectsV2Async(request);
			keys.AddRange((response.S3Objects ?? []).Select(item => item.Key[prefix.Length..]));
			request.ContinuationToken = response.NextContinuationToken;
		}
		while (response.IsTruncated == true);

		return keys;
	}

	public Task DeleteAsync(string key) => client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = KeyOf(key) });
}

/// <summary>
/// <see cref="IRecordTable"/> on a DynamoDB table with the string keys <c>pk</c> and <c>sk</c>: a record's version is the
/// number <c>v</c>, its values the strings <c>x_&lt;name&gt;</c>. Reads are strongly consistent; a batch of writes is one
/// transaction whose conditions are the expected versions.
/// </summary>
internal sealed class DynamoRecordTable(IAmazonDynamoDB client, string table) : IRecordTable
{
	private const string ValuePrefix = "x_";

	public string Location => $"dynamodb:{table}";

	/// <summary>Creates the table (on demand capacity) unless it exists, and waits until it is usable.</summary>
	public async Task EnsureTableAsync()
	{
		try
		{
			await client.DescribeTableAsync(table);
			return;
		}
		catch (ResourceNotFoundException)
		{
		}

		await client.CreateTableAsync(new CreateTableRequest
		{
			TableName = table,
			BillingMode = BillingMode.PAY_PER_REQUEST,
			AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
			KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)]
		});
		for (var attempt = 0; attempt < 60 && (await client.DescribeTableAsync(table)).Table.TableStatus != TableStatus.ACTIVE; attempt++)
		{
			await Task.Delay(500);
		}
	}

	public async Task<CloudRecord?> GetAsync(string partition, string sort)
	{
		var response = await client.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(partition, sort), ConsistentRead = true });
		return response.Item is { Count: > 0 } item ? Record(item) : null;
	}

	public async Task<IReadOnlyList<CloudRecord>> QueryAsync(string partition, string sortPrefix = "")
	{
		var records = new List<CloudRecord>();
		var request = new QueryRequest
		{
			TableName = table,
			ConsistentRead = true,
			KeyConditionExpression = sortPrefix.Length == 0 ? "pk = :p" : "pk = :p AND begins_with(sk, :s)",
			ExpressionAttributeValues = sortPrefix.Length == 0
				? new Dictionary<string, AttributeValue> { [":p"] = new(partition) }
				: new Dictionary<string, AttributeValue> { [":p"] = new(partition), [":s"] = new(sortPrefix) }
		};
		QueryResponse response;
		do
		{
			response = await client.QueryAsync(request);
			records.AddRange((response.Items ?? []).Select(Record));
			request.ExclusiveStartKey = response.LastEvaluatedKey;
		}
		while (response.LastEvaluatedKey is { Count: > 0 });

		return records;
	}

	public async Task WriteAsync(IReadOnlyList<CloudWrite> writes)
	{
		if (writes.Count == 0)
		{
			return;
		}

		if (writes.Count > IRecordTable.MaxWrites)
		{
			throw new StorageException($"A batch of {writes.Count} writes is more than one transaction allows ({IRecordTable.MaxWrites}); nothing was written.");
		}

		var items = writes.Select(Item).ToList();
		try
		{
			await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items });
		}
		catch (TransactionCanceledException ex) when (ex.CancellationReasons?.Any(reason => reason.Code == "ConditionalCheckFailed") == true)
		{
			var failed = ex.CancellationReasons.Select((reason, index) => (reason, index)).Where(pair => pair.reason.Code == "ConditionalCheckFailed")
				.Select(pair => $"{writes[pair.index].Partition}/{writes[pair.index].Sort}");
			throw new CloudConflictException($"Record(s) {string.Join(", ", failed)} changed meanwhile; nothing was written.");
		}
	}

	private TransactWriteItem Item(CloudWrite write)
	{
		var names = new Dictionary<string, string>();
		var values = new Dictionary<string, AttributeValue>();
		string? condition = null;
		if (write.ExpectedVersion is 0)
		{
			condition = "attribute_not_exists(pk)";
		}
		else if (write.ExpectedVersion is { } expected)
		{
			names["#v"] = "v";
			values[":expected"] = new AttributeValue { N = expected.ToString(System.Globalization.CultureInfo.InvariantCulture) };
			condition = "#v = :expected";
		}

		if (write.Values is null)
		{
			return new TransactWriteItem
			{
				Delete = new Amazon.DynamoDBv2.Model.Delete
				{
					TableName = table,
					Key = Key(write.Partition, write.Sort),
					ConditionExpression = condition,
					ExpressionAttributeNames = names.Count == 0 ? null : names,
					ExpressionAttributeValues = values.Count == 0 ? null : values
				}
			};
		}

		names["#v"] = "v";
		values[":zero"] = new AttributeValue { N = "0" };
		values[":one"] = new AttributeValue { N = "1" };
		var assignments = new List<string> { "#v = if_not_exists(#v, :zero) + :one" };
		var index = 0;
		foreach (var (name, value) in write.Values)
		{
			names[$"#a{index}"] = ValuePrefix + name;
			values[$":a{index}"] = new AttributeValue(value);
			assignments.Add($"#a{index} = :a{index}");
			index++;
		}

		return new TransactWriteItem
		{
			Update = new Update
			{
				TableName = table,
				Key = Key(write.Partition, write.Sort),
				UpdateExpression = "SET " + string.Join(", ", assignments),
				ConditionExpression = condition,
				ExpressionAttributeNames = names,
				ExpressionAttributeValues = values
			}
		};
	}

	private static Dictionary<string, AttributeValue> Key(string partition, string sort) => new() { ["pk"] = new(partition), ["sk"] = new(sort) };

	private static CloudRecord Record(Dictionary<string, AttributeValue> item) => new(
		item["pk"].S,
		item["sk"].S,
		item.Where(pair => pair.Key.StartsWith(ValuePrefix, StringComparison.Ordinal)).ToDictionary(pair => pair.Key[ValuePrefix.Length..], pair => pair.Value.S, StringComparer.Ordinal),
		long.Parse(item["v"].N, System.Globalization.CultureInfo.InvariantCulture));
}
