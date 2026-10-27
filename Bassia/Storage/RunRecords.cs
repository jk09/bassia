namespace Bassia.Storage;

/// <summary>
/// The run-record half of <see cref="IRunRecordStore"/> on any store's record primitives: what a store that keeps
/// records elsewhere (a database, a server) needs to implement it.
/// </summary>
internal static class RunRecords
{
	public static async Task<IReadOnlyList<RunMetadata>> ListLatestAsync(IRecordStore store)
	{
		var runs = new List<RunMetadata>();
		foreach (var key in await store.ListKeysAsync(RunMetadata.RefPrefix))
		{
			if (await LoadLatestAsync(store, RunMetadata.NormalizeRunId(key)) is { } metadata)
			{
				runs.Add(metadata);
			}
		}

		return runs.OrderByDescending(run => run.Created, StringComparer.Ordinal).ToList();
	}

	public static async Task<RunMetadata?> LoadLatestAsync(IRecordStore store, string runId)
	{
		if (await store.ReadLatestAsync(RunMetadata.RefBase(runId), RunMetadata.FileName) is not { } found)
		{
			return null;
		}

		var metadata = RunMetadata.FromToml(found.Content);
		metadata.Lineage = found.Lineage;
		return metadata;
	}

	public static async Task<string> CommitAsync(IRecordStore store, RunMetadata metadata)
	{
		metadata.Lineage = await store.LatestIndexAsync(RunMetadata.RefBase(metadata.RunId)) + 1;
		return await store.WriteAsync(RunMetadata.RefBase(metadata.RunId), metadata.Lineage, RunMetadata.FileName, metadata.ToToml(),
			$"{metadata.RunId} #{metadata.Lineage}: {metadata.Status}");
	}
}
