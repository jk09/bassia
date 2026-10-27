namespace Bassia.Server.Api;

using Bassia.Locks;
using Bassia.Server.Auth;
using Bassia.Storage;
using Bassia.Storage.Remote;

/// <summary>
/// The stateful contracts over HTTP: records (runs and integrations), the lock table, jobs and their output, and the
/// task cache. A caller sees what is about components they may read; changing a component's records, locks and jobs
/// takes <c>run</c> on it.
/// </summary>
internal static class StateEndpoints
{
	public static void Map(RouteGroupBuilder api, ServerState state)
	{
		// ----- records -----

		api.MapGet("/records/index", async (string @ref, HttpContext context) =>
		{
			// A record about a component the caller may not read does not exist for them.
			var latest = await state.Records.LatestIndexAsync(@ref);
			return ServerApp.Json(new IndexResponse(latest >= 0 && !await VisibleAsync(state, @ref, await state.ReadableAsync(ServerApp.CallerOf(context))) ? -1 : latest));
		});
		api.MapGet("/records/keys", async (string prefix, HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			var readable = await state.ReadableAsync(caller);
			var keys = new List<string>();
			foreach (var key in await state.Records.ListKeysAsync(prefix))
			{
				if (await VisibleAsync(state, prefix + key, readable))
				{
					keys.Add(key);
				}
			}

			return ServerApp.Json(new KeysResponse(keys));
		});
		api.MapGet("/records/latest", async (string @ref, string file, HttpContext context) =>
		{
			if (await state.Records.ReadLatestAsync(@ref, file) is not { } found
				|| !ServerState.ComponentsOfRecord(found.Content).All((await state.ReadableAsync(ServerApp.CallerOf(context))).Contains))
			{
				return ServerApp.Error(StatusCodes.Status404NotFound, $"No record '{@ref}'.");
			}

			return ServerApp.Json(new RecordContent(found.Content, found.Lineage));
		});
		api.MapPost("/records", async (RecordWrite write, HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			await state.RequireAsync(caller, ServerState.ComponentsOfRecord(write.Content), Permission.Run);
			var name = await state.Records.WriteAsync(write.RefBase, write.Lineage, write.FileName, write.Content, write.Message);
			await state.Access.AuditAsync(caller.Id, "record.write", name, write.Message, ServerApp.RemoteOf(context));
			return ServerApp.Json(new NameResponse(name));
		});
		api.MapGet("/runs", async (HttpContext context) =>
		{
			var readable = await state.ReadableAsync(ServerApp.CallerOf(context));
			return ServerApp.Json((await state.Records.ListLatestAsync())
				.Where(run => run.Components.All(component => readable.Contains(component.Name)))
				.Select(run => new RecordContent(run.ToToml(), run.Lineage))
				.ToList());
		});

		// ----- locks -----

		api.MapGet("/locks", async (HttpContext context) =>
		{
			var readable = await state.ReadableAsync(ServerApp.CallerOf(context));
			var (version, entries) = await state.Locks.ReadTableAsync();
			return ServerApp.Json(new LockTableResponse(version, entries.Where(entry => Bassia.Storage.Remote.Api.ComponentsOf(entry).All(readable.Contains))
				.Select(entry => entry.ToToml()).ToList()));
		});
		api.MapPost("/locks", async (LockCommit commit, HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			var saved = commit.Saved.Select(LockEntry.FromToml).ToList();
			var touched = new List<string>();
			foreach (var entry in saved)
			{
				touched.AddRange(Bassia.Storage.Remote.Api.ComponentsOf(entry));
			}

			foreach (var runId in saved.Select(entry => entry.RunId).Concat(commit.Deleted))
			{
				if (await state.Locks.FindAsync(runId) is { } existing)
				{
					touched.AddRange(Bassia.Storage.Remote.Api.ComponentsOf(existing));
				}
			}

			await state.RequireAsync(caller, touched, Permission.Read);
			return await state.Locks.CommitAsync(commit.ExpectedVersion, saved, commit.Deleted)
				? Results.NoContent()
				: ServerApp.Error(StatusCodes.Status409Conflict, "The lock table changed meanwhile; decide again.");
		});

		// ----- jobs -----

		api.MapPost("/jobs", async (JobRegistration registration, HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			await state.RequireAsync(caller, registration.Components ?? [], Permission.Run);
			await state.Jobs.RegisterAsync(registration, caller.Id);
			return Results.NoContent();
		});
		api.MapPost("/jobs/{id}/heartbeat", async (string id, HttpContext context) =>
		{
			await OwnAsync(state, context, id);
			return ServerApp.Json(new { stop = await state.Jobs.HeartbeatAsync(id) });
		});
		api.MapPost("/jobs/{id}/log", async (string id, LogChunk chunk, HttpContext context) =>
		{
			await OwnAsync(state, context, id);
			await state.Jobs.AppendLogAsync(id, chunk.Text);
			return Results.NoContent();
		});
		api.MapPost("/jobs/{id}/finish", async (string id, HttpContext context) =>
		{
			await OwnAsync(state, context, id);
			await state.Jobs.FinishAsync(id);
			return Results.NoContent();
		});
		api.MapGet("/jobs", async (string kind, HttpContext context) =>
		{
			var readable = await state.ReadableAsync(ServerApp.CallerOf(context));
			var visible = new List<JobInfo>();
			foreach (var found in await state.Jobs.ListAsync(kind))
			{
				if ((await ComponentsOfJobAsync(state, found.Job.Id, found.Components)).All(readable.Contains))
				{
					visible.Add(found.Job);
				}
			}

			return ServerApp.Json(visible);
		});
		api.MapGet("/jobs/{id}", async (string id, HttpContext context) => ServerApp.Json((await VisibleJobAsync(state, context, id)).Job));
		api.MapGet("/jobs/{id}/log", async (string id, long? offset, HttpContext context) =>
		{
			await VisibleJobAsync(state, context, id);
			return ServerApp.Json(await state.Jobs.ReadLogAsync(id, offset ?? 0));
		});
		api.MapPost("/jobs/{id}/stop", async (string id, HttpContext context) =>
		{
			var (job, components, _) = await VisibleJobAsync(state, context, id);
			var caller = ServerApp.CallerOf(context);
			await state.RequireAsync(caller, components, Permission.Run);
			await state.Jobs.SetStopAsync(id, true);
			await state.Access.AuditAsync(caller.Id, "job.stop", job.Id, job.Kind, ServerApp.RemoteOf(context));
			return Results.NoContent();
		});
		api.MapDelete("/jobs/{id}/stop", async (string id, HttpContext context) =>
		{
			await VisibleJobAsync(state, context, id);
			await state.Jobs.SetStopAsync(id, false);
			return Results.NoContent();
		});

		// ----- task cache -----

		api.MapGet("/task-cache/{key}", async (string key, HttpContext context) =>
		{
			var file = Path.Combine(state.Config.State, "scratch", $"cache-{Guid.NewGuid():N}.zip");
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			if (!await state.TaskCache.GetAsync(Safe(key), file))
			{
				return ServerApp.Error(StatusCodes.Status404NotFound, $"No cached result '{key}'.");
			}

			context.Response.RegisterForDispose(new TemporaryFile(file));
			return Results.File(file, "application/zip");
		});
		api.MapPut("/task-cache/{key}", async (string key, HttpContext context) =>
		{
			var file = Path.Combine(state.Config.State, "scratch", $"cache-{Guid.NewGuid():N}.zip");
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			try
			{
				await using (var target = File.Create(file))
				{
					await context.Request.Body.CopyToAsync(target);
				}

				await state.TaskCache.PutAsync(Safe(key), file);
			}
			finally
			{
				File.Delete(file);
			}

			return Results.NoContent();
		});
		api.MapGet("/task-cache", async () => ServerApp.Json(await state.TaskCache.SizeAsync()));
		api.MapDelete("/task-cache", async (HttpContext context) =>
		{
			var caller = ServerApp.CallerOf(context);
			await state.RequireGlobalAsync(caller, Permission.Admin);
			await state.TaskCache.ClearAsync();
			await state.Access.AuditAsync(caller.Id, "task-cache.clear", "*", "", ServerApp.RemoteOf(context));
			return Results.NoContent();
		});
	}

	/// <summary>A cache key as an object key: the keys are hashes, so anything else is refused.</summary>
	private static string Safe(string key) =>
		key.Length is > 0 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? key : throw new ArgumentException($"'{key}' is not a task cache key.");

	private static async Task<bool> VisibleAsync(ServerState state, string refBase, Readable readable)
	{
		var latest = await state.Records.LatestIndexAsync(refBase);
		if (latest < 0)
		{
			return false;
		}

		var content = (await state.Records.QueryContentAsync(refBase, latest)) ?? "";
		return ServerState.ComponentsOfRecord(content).All(readable.Contains);
	}

	/// <summary>The job, when the caller may read every component it works on; otherwise it does not exist for them.</summary>
	private static async Task<(JobInfo Job, IReadOnlyList<string> Components, string Owner)> VisibleJobAsync(ServerState state, HttpContext context, string id)
	{
		if (await state.Jobs.FindAsync(id) is { } found)
		{
			var components = await ComponentsOfJobAsync(state, id, found.Components);
			if (components.All((await state.ReadableAsync(ServerApp.CallerOf(context))).Contains))
			{
				return (found.Job, components, found.Owner);
			}
		}

		throw new NotFoundException($"No job '{id}'.");
	}

	/// <summary>
	/// The components a job works on: those it registered with, else those of the run or integration record of the same
	/// id - a run's job registers before it knows them, so until its record exists it is visible to everyone allowed in.
	/// </summary>
	private static async Task<IReadOnlyList<string>> ComponentsOfJobAsync(ServerState state, string id, IReadOnlyList<string> registered)
	{
		if (registered.Count > 0)
		{
			return registered;
		}

		foreach (var refBase in new[] { RunMetadata.RefBase(id), Bassia.Integration.IntegrationRecord.RefBase(id) })
		{
			var latest = await state.Records.LatestIndexAsync(refBase);
			if (latest >= 0 && await state.Records.QueryContentAsync(refBase, latest) is { } content)
			{
				return ServerState.ComponentsOfRecord(content);
			}
		}

		return [];
	}

	/// <summary>Only the user who registered a job renews it, writes its output and ends it.</summary>
	private static async Task OwnAsync(ServerState state, HttpContext context, string id)
	{
		var found = await VisibleJobAsync(state, context, id);
		if (found.Owner != ServerApp.CallerOf(context).Id)
		{
			throw new ForbiddenException($"Job '{id}' is '{found.Owner}''s.");
		}
	}

	private sealed class TemporaryFile(string path) : IDisposable
	{
		public void Dispose() => File.Delete(path);
	}
}
