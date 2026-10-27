namespace Bassia.Storage.Cloud;

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Bassia.Git;
using Bassia.Storage.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// Git's smart HTTP protocol over a <see cref="CloudComponentStorage"/>: <c>&lt;prefix&gt;/&lt;component&gt;</c> is a
/// repository git clones, fetches (protocol v0 and v2, filters and objects by id for partial clones) and pushes to; and,
/// when given, <c>&lt;prefix&gt;/.bassia</c> is a meta-repo folder served read-only, so the prefix URL is a depot
/// <c>bassia clone</c> takes.
/// </summary>
/// <remarks>
/// Fetches run git's upload-pack on the node's cache, brought up to the table first. A push runs git's receive-pack on a
/// scratch repository that borrows the cache's objects and starts with its refs; the refs it accepted are then
/// published under compare-and-swap - each must still be where the client saw it - and the gateway reports the outcome
/// itself: every ref updated, or (a race lost) none.
/// </remarks>
internal sealed class GitGateway(CloudComponentStorage storage, string? metaRepoDir = null)
{
	private const string ZeroId = "0000000000000000000000000000000000000000";

	public WebApplication Build(string url, string prefix = "/git")
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls(url);
		builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
		var app = builder.Build();
		Map(app, prefix);
		return app;
	}

	public void Map(IEndpointRouteBuilder app, string prefix = "/git")
	{
		app.MapGet(prefix + "/{name}/info/refs", InfoRefsAsync);
		app.MapPost(prefix + "/{name}/git-upload-pack", UploadPackAsync);
		app.MapPost(prefix + "/{name}/git-receive-pack", ReceivePackAsync);
	}

	/// <summary>The repository folder <paramref name="name"/> names, brought up to date; null when there is none (or, for a push, none to write).</summary>
	private async Task<string?> RepositoryAsync(string name, bool push)
	{
		name = name.EndsWith(".git", StringComparison.Ordinal) ? name[..^4] : name;
		if (name == Monorepo.MetaRepoFolderName)
		{
			return push ? null : metaRepoDir;
		}

		if (name.StartsWith('.') || !storage.Exists(name))
		{
			return null;
		}

		await storage.SyncAsync(name);
		return storage.LocationOf(name);
	}

	private static string Name(string name) => name.EndsWith(".git", StringComparison.Ordinal) ? name[..^4] : name;

	private static string? Protocol(HttpContext context) => context.Request.Headers["Git-Protocol"].FirstOrDefault();

	private static void NoCache(HttpResponse response)
	{
		response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate";
		response.Headers.Pragma = "no-cache";
	}

	private static async Task RefuseAsync(HttpContext context, int status, string message)
	{
		context.Response.StatusCode = status;
		context.Response.ContentType = "text/plain";
		await context.Response.WriteAsync(message + "\n");
	}

	private async Task InfoRefsAsync(HttpContext context, string name)
	{
		var service = context.Request.Query["service"].FirstOrDefault();
		if (service is not ("git-upload-pack" or "git-receive-pack"))
		{
			await RefuseAsync(context, StatusCodes.Status403Forbidden, "Only git's smart HTTP protocol is served: ask for ?service=git-upload-pack or git-receive-pack.");
			return;
		}

		if (await RepositoryAsync(name, service == "git-receive-pack") is not { } directory)
		{
			await RefuseAsync(context, StatusCodes.Status404NotFound, $"No repository '{Name(name)}' to {(service == "git-receive-pack" ? "push to" : "fetch from")}.");
			return;
		}

		NoCache(context.Response);
		context.Response.ContentType = $"application/x-{service}-advertisement";
		var protocol = Protocol(context);
		if (protocol?.Contains("version=2", StringComparison.Ordinal) != true)
		{
			await context.Response.Body.WriteAsync(PktLine.Encode($"# service={service}\n"));
			await context.Response.Body.WriteAsync(PktLine.Flush);
		}

		await RunAsync(directory, [service[4..], "--stateless-rpc", "--advertise-refs", "."], null, context.Response.Body, protocol, context.RequestAborted);
	}

	private async Task UploadPackAsync(HttpContext context, string name)
	{
		if (await RepositoryAsync(name, push: false) is not { } directory)
		{
			await RefuseAsync(context, StatusCodes.Status404NotFound, $"No repository '{Name(name)}'.");
			return;
		}

		NoCache(context.Response);
		context.Response.ContentType = "application/x-git-upload-pack-result";
		await using var body = RequestBody(context);
		await RunAsync(directory, ["upload-pack", "--stateless-rpc", "."], body, context.Response.Body, Protocol(context), context.RequestAborted);
	}

	/// <summary>See the class remarks: receive-pack on a scratch repository, then one publish of every ref it accepted.</summary>
	private async Task ReceivePackAsync(HttpContext context, string name)
	{
		if (await RepositoryAsync(name, push: true) is not { } directory)
		{
			await RefuseAsync(context, StatusCodes.Status404NotFound, $"No repository '{Name(name)}' to push to.");
			return;
		}

		name = Name(name);
		using var request = new MemoryStream();
		await using (var body = RequestBody(context))
		{
			await body.CopyToAsync(request, context.RequestAborted);
		}

		var (commands, capabilities) = PushCommands(request.ToArray());
		var scratch = Path.Combine(storage.CacheRoot, ".push", Guid.NewGuid().ToString("N"));
		var statuses = new Dictionary<string, string?>(StringComparer.Ordinal);
		string unpack;
		try
		{
			unpack = await ReceiveAsync(directory, scratch, request, commands, statuses);
			var accepted = commands.Where(command => statuses[command.Ref] is null).ToList();
			if (accepted.Count > 0)
			{
				try
				{
					await storage.PublishAsync(name, accepted.Select(command => new RefUpdate(
						command.Ref,
						command.New == ZeroId ? null : command.New,
						command.Old == ZeroId ? RefExpect.Absent : RefExpect.Value,
						command.Old)).ToList());
				}
				catch (StorageException ex)
				{
					var reason = ex.Message.ReplaceLineEndings(" ");
					foreach (var command in commands)
					{
						statuses[command.Ref] = statuses[command.Ref] ?? $"{reason} No ref of this push was updated.";
					}
				}
			}
		}
		finally
		{
			GitComponentStorage.DeleteDirectory(scratch);
		}

		NoCache(context.Response);
		context.Response.ContentType = "application/x-git-receive-pack-result";
		if (capabilities.Contains("report-status") || capabilities.Contains("report-status-v2"))
		{
			await context.Response.Body.WriteAsync(Report(unpack, commands, statuses, capabilities));
		}
	}

	/// <summary>
	/// Runs receive-pack on <paramref name="scratch"/> - a bare repository borrowing the cache's objects, with its
	/// branches and tags - and records why each command it did not apply failed (null: applied). The objects it received
	/// are moved into the cache, unreferenced until published. Returns the unpack status.
	/// </summary>
	private static async Task<string> ReceiveAsync(string cacheDir, string scratch, MemoryStream request, IReadOnlyList<PushCommand> commands, Dictionary<string, string?> statuses)
	{
		var cacheObjects = Path.Combine(cacheDir, ".git", "objects");
		await GitClient.In(Directory.CreateDirectory(Path.GetDirectoryName(scratch)!).FullName).RunOrThrowAsync(["init", "--bare", "--quiet", scratch]);
		await File.WriteAllTextAsync(Path.Combine(scratch, "objects", "info", "alternates"), Path.GetFullPath(cacheObjects).Replace('\\', '/') + "\n");
		var git = GitClient.In(scratch);
		var before = await CloudComponentStorage.LocalRefsAsync(GitClient.In(cacheDir));
		if (before.Count > 0)
		{
			await git.RunOrThrowAsync(["update-ref", "--stdin"], string.Concat(before.Select(pair => $"update {pair.Key} {pair.Value}\n")));
		}

		// Keep what arrives as a pack, so it moves to the cache as one; check every object.
		await git.RunOrThrowAsync(["config", "receive.unpackLimit", "1"]);
		await git.RunOrThrowAsync(["config", "receive.fsckObjects", "true"]);

		request.Position = 0;
		var (exitCode, error) = await RunAsync(scratch, ["receive-pack", "--stateless-rpc", "."], request, Stream.Null, null, CancellationToken.None);
		var after = await CloudComponentStorage.LocalRefsAsync(git, "refs");
		foreach (var command in commands)
		{
			statuses[command.Ref] = command switch
			{
				_ when !command.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) && !command.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) => "only branches and tags are pushed",
				_ when after.GetValueOrDefault(command.Ref) != (command.New == ZeroId ? null : command.New) => exitCode != 0 ? "unpacker error" : "refused by the server's checks",
				_ => null
			};
		}

		var packDir = Path.Combine(scratch, "objects", "pack");
		foreach (var pack in Directory.Exists(packDir) ? Directory.GetFiles(packDir, "*.pack") : [])
		{
			// The index last: git uses a pack only once its index is there.
			foreach (var file in new[] { pack, Path.ChangeExtension(pack, ".idx") })
			{
				File.Copy(file, Path.Combine(cacheObjects, "pack", Path.GetFileName(file)), overwrite: true);
			}
		}

		return exitCode == 0 ? "ok" : $"failed: {error.Trim().ReplaceLineEndings(" ")}";
	}

	private static Stream RequestBody(HttpContext context)
	{
		if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
		{
			limit.MaxRequestBodySize = null;
		}

		return context.Request.Headers.ContentEncoding.Any(encoding => encoding?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true)
			? new GZipStream(context.Request.Body, CompressionMode.Decompress)
			: context.Request.Body;
	}

	/// <summary>The commands of a push request (<c>old new ref</c>, before its pack) and the capabilities the client asked for.</summary>
	internal static (IReadOnlyList<PushCommand> Commands, IReadOnlySet<string> Capabilities) PushCommands(byte[] request)
	{
		var commands = new List<PushCommand>();
		var capabilities = new HashSet<string>(StringComparer.Ordinal);
		foreach (var line in PktLine.ReadUntilFlush(request))
		{
			var text = line.TrimEnd('\n');
			var nul = text.IndexOf('\0', StringComparison.Ordinal);
			if (nul >= 0)
			{
				capabilities.UnionWith(text[(nul + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
				text = text[..nul];
			}

			var fields = text.Split(' ');
			if (fields.Length == 3 && !text.StartsWith("shallow ", StringComparison.Ordinal))
			{
				commands.Add(new PushCommand(fields[0], fields[1], fields[2]));
			}
		}

		return (commands, capabilities);
	}

	/// <summary>Git's report-status: the unpack status and <c>ok</c> or <c>ng</c> per ref, in band 1 when the client asked for side bands.</summary>
	internal static byte[] Report(string unpack, IReadOnlyList<PushCommand> commands, IReadOnlyDictionary<string, string?> statuses, IReadOnlySet<string> capabilities)
	{
		using var report = new MemoryStream();
		report.Write(PktLine.Encode($"unpack {unpack}\n"));
		foreach (var command in commands)
		{
			report.Write(PktLine.Encode(statuses[command.Ref] is { } reason ? $"ng {command.Ref} {reason}\n" : $"ok {command.Ref}\n"));
		}

		report.Write(PktLine.Flush);
		var band = capabilities.Contains("side-band-64k") ? 65515 : capabilities.Contains("side-band") ? 995 : 0;
		if (band == 0)
		{
			return report.ToArray();
		}

		using var wrapped = new MemoryStream();
		foreach (var chunk in report.ToArray().Chunk(band))
		{
			wrapped.Write(PktLine.Encode([1, .. chunk]));
		}

		wrapped.Write(PktLine.Flush);
		return wrapped.ToArray();
	}

	/// <summary>Runs git in <paramref name="directory"/> streaming <paramref name="input"/> in and its output to <paramref name="output"/>.</summary>
	private static async Task<(int ExitCode, string Error)> RunAsync(string directory, IReadOnlyList<string> arguments, Stream? input, Stream output, string? protocol, CancellationToken cancellation)
	{
		var start = new ProcessStartInfo("git")
		{
			WorkingDirectory = directory,
			RedirectStandardInput = input is not null,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		if (protocol is not null)
		{
			start.Environment["GIT_PROTOCOL"] = protocol;
		}

		using var process = Process.Start(start) ?? throw new GitException("git could not be started.");
		var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellation);
		var error = process.StandardError.ReadToEndAsync(cancellation);
		if (input is not null)
		{
			try
			{
				await input.CopyToAsync(process.StandardInput.BaseStream, cancellation);
				process.StandardInput.Close();
			}
			catch (IOException)
			{
				// git stopped reading (it failed); its exit code and stderr say why.
			}
		}

		await process.WaitForExitAsync(cancellation);
		await copy;
		return (process.ExitCode, await error);
	}
}

/// <summary>A command of a push: move <see cref="Ref"/> from <see cref="Old"/> to <see cref="New"/> (zeros: none).</summary>
internal sealed record PushCommand(string Old, string New, string Ref);

/// <summary>Git's pkt-line framing: four hex digits of length (themselves included), then the payload; <c>0000</c> flushes.</summary>
internal static class PktLine
{
	public static readonly byte[] Flush = "0000"u8.ToArray();

	public static byte[] Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

	public static byte[] Encode(byte[] payload) => [.. Encoding.ASCII.GetBytes((payload.Length + 4).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)), .. payload];

	/// <summary>The lines from the start of <paramref name="data"/> to the first flush, decoded as UTF-8.</summary>
	public static IEnumerable<string> ReadUntilFlush(byte[] data)
	{
		var position = 0;
		while (position + 4 <= data.Length)
		{
			var length = Convert.ToInt32(Encoding.ASCII.GetString(data, position, 4), 16);
			if (length < 4)
			{
				yield break;
			}

			yield return Encoding.UTF8.GetString(data, position + 4, length - 4);
			position += length;
		}
	}
}
