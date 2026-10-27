namespace Bassia.Storage.Remote;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// What <c>bassia login</c> stored for one server: a token as given (<c>token</c>), the server's own tokens with a
/// refresh token (<c>server</c>, after a GitHub login), an OIDC issuer's tokens with a refresh token (<c>oidc</c>), or an
/// OIDC client's credentials to get tokens with (<c>oidc-client</c>, for workers).
/// </summary>
internal sealed record ServerCredential(string Server, string Kind, string? AccessToken = null, string? RefreshToken = null, DateTimeOffset? Expires = null,
	string? TokenEndpoint = null, string? ClientId = null, string? ClientSecret = null, string? Scopes = null);

/// <summary>
/// The credentials of the servers this user logged in to, and fresh access tokens from them. <c>BASSIA_TOKEN</c>, when
/// set, is the token for every server (a worker's). Credentials are kept in the OS keychain where there is one
/// (macOS Keychain, the Secret Service on Linux, the Windows Credential Manager), else in a file only the user can
/// read (<c>~/.bassia/credentials</c>); <c>BASSIA_CREDENTIAL_STORE=file</c> chooses the file.
/// </summary>
internal static class ServerCredentials
{
	public const string TokenVariable = "BASSIA_TOKEN";

	private const string Service = "bassia";

	/// <summary>The server URL as credentials are kept under: scheme, host and path, without a trailing slash.</summary>
	public static string Normalize(string server) => server.Trim().TrimEnd('/');

	public static async Task<ServerCredential?> LoadAsync(string server)
	{
		var text = await StoreFor().ReadAsync(Normalize(server));
		return text is null ? null : JsonSerializer.Deserialize<ServerCredential>(text, Api.Json);
	}

	public static Task SaveAsync(ServerCredential credential) =>
		StoreFor().WriteAsync(Normalize(credential.Server), JsonSerializer.Serialize(credential with { Server = Normalize(credential.Server) }, Api.Json));

	public static Task<bool> RemoveAsync(string server) => StoreFor().DeleteAsync(Normalize(server));

	/// <summary>Every server with a stored credential (what <c>bassia credential get</c> matches a host against).</summary>
	public static Task<IReadOnlyList<string>> ServersAsync() => StoreFor().ListAsync();

	/// <summary>
	/// A valid access token for <paramref name="server"/>: <c>BASSIA_TOKEN</c>, or the stored one - refreshed first when it
	/// expires within a minute. Null when the user never logged in.
	/// </summary>
	public static async Task<string?> AccessTokenAsync(string server, HttpClient http)
	{
		if (Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnvironment)
		{
			return fromEnvironment;
		}

		if (await LoadAsync(server) is not { } credential)
		{
			return null;
		}

		if (credential.Expires is { } expires && expires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(1))
		{
			credential = await RefreshAsync(credential, http);
			await SaveAsync(credential);
		}

		return credential.AccessToken;
	}

	/// <summary>A new access token for the credential: from the server, the issuer's token endpoint, or the client's credentials.</summary>
	private static async Task<ServerCredential> RefreshAsync(ServerCredential credential, HttpClient http)
	{
		switch (credential.Kind)
		{
			case "server" when credential.RefreshToken is { } refresh:
				using (var response = await http.PostAsJsonAsync(Normalize(credential.Server) + Api.Prefix + "/auth/refresh", new RefreshRequest(refresh), Api.Json))
				{
					if (!response.IsSuccessStatusCode)
					{
						throw new MonorepoException($"The login to {credential.Server} expired; log in again with 'bassia login -server {credential.Server}'.");
					}

					var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(Api.Json))!;
					return credential with { AccessToken = token.AccessToken, RefreshToken = token.RefreshToken ?? refresh, Expires = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn) };
				}

			case "oidc" when credential.RefreshToken is { } refresh && credential.TokenEndpoint is { } endpoint:
				return await Oidc.TokenAsync(http, endpoint, new Dictionary<string, string>
				{
					["grant_type"] = "refresh_token",
					["refresh_token"] = refresh,
					["client_id"] = credential.ClientId ?? ""
				}, credential);

			case "oidc-client" when credential.TokenEndpoint is { } endpoint:
				return await Oidc.TokenAsync(http, endpoint, new Dictionary<string, string>
				{
					["grant_type"] = "client_credentials",
					["client_id"] = credential.ClientId ?? "",
					["client_secret"] = credential.ClientSecret ?? "",
					["scope"] = credential.Scopes ?? ""
				}, credential);

			default:
				throw new MonorepoException($"The token for {credential.Server} expired; log in again with 'bassia login -server {credential.Server}'.");
		}
	}

	// ----- where credentials are kept -----

	private interface ICredentialStore
	{
		Task<string?> ReadAsync(string server);

		Task WriteAsync(string server, string secret);

		Task<bool> DeleteAsync(string server);

		Task<IReadOnlyList<string>> ListAsync();
	}

	/// <summary>The OS keychain when there is one; the file otherwise. The index of servers is always the file's.</summary>
	private static ICredentialStore StoreFor()
	{
		var file = new FileStore(Environment.GetEnvironmentVariable("BASSIA_CREDENTIALS_DIR") is { Length: > 0 } dir
			? dir
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bassia", "credentials"));
		if (Environment.GetEnvironmentVariable("BASSIA_CREDENTIAL_STORE") == "file")
		{
			return file;
		}

		if (OperatingSystem.IsMacOS() && OnPath("security"))
		{
			return new KeychainStore(file, new MacKeychain());
		}

		if (OperatingSystem.IsLinux() && OnPath("secret-tool") && Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 })
		{
			return new KeychainStore(file, new SecretService());
		}

		if (OperatingSystem.IsWindows())
		{
			return new KeychainStore(file, new WindowsCredentials());
		}

		return file;
	}

	private static bool OnPath(string tool) =>
		(Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, tool)));

	/// <summary>One file per server, readable by the user only; the file's name is a hash of the server URL.</summary>
	private sealed class FileStore(string dir) : ICredentialStore
	{
		private string PathOf(string server) => Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server)))[..24].ToLowerInvariant() + ".json");

		public async Task<string?> ReadAsync(string server) => File.Exists(PathOf(server)) ? Unwrap(await File.ReadAllTextAsync(PathOf(server))).Secret : null;

		public async Task WriteAsync(string server, string secret) => await WriteRawAsync(server, JsonSerializer.Serialize(new { server, secret }));

		public async Task WriteRawAsync(string server, string text)
		{
			Directory.CreateDirectory(dir);
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}

			var partial = PathOf(server) + ".tmp";
			await File.WriteAllTextAsync(partial, "");
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			await File.WriteAllTextAsync(partial, text);
			File.Move(partial, PathOf(server), overwrite: true);
		}

		public Task<bool> DeleteAsync(string server)
		{
			var existed = File.Exists(PathOf(server));
			File.Delete(PathOf(server));
			return Task.FromResult(existed);
		}

		public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>(
			Directory.Exists(dir)
				? Directory.EnumerateFiles(dir, "*.json").Select(file => Unwrap(File.ReadAllText(file)).Server).OfType<string>().Order(StringComparer.Ordinal).ToList()
				: []);

		private static (string? Server, string? Secret) Unwrap(string text)
		{
			using var document = JsonDocument.Parse(text);
			var root = document.RootElement;
			return (root.TryGetProperty("server", out var server) ? server.GetString() : null,
				root.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.String ? secret.GetString() : null);
		}
	}

	private interface IKeychain
	{
		Task<string?> ReadAsync(string server);

		Task<bool> WriteAsync(string server, string secret);

		Task DeleteAsync(string server);
	}

	/// <summary>Secrets in the keychain, the list of servers (no secret) in the file store.</summary>
	private sealed class KeychainStore(FileStore index, IKeychain keychain) : ICredentialStore
	{
		public async Task<string?> ReadAsync(string server) => await keychain.ReadAsync(server) ?? await index.ReadAsync(server);

		public async Task WriteAsync(string server, string secret)
		{
			if (await keychain.WriteAsync(server, secret))
			{
				await index.WriteRawAsync(server, JsonSerializer.Serialize(new { server, keychain = true }));
			}
			else
			{
				await index.WriteAsync(server, secret);
			}
		}

		public async Task<bool> DeleteAsync(string server)
		{
			await keychain.DeleteAsync(server);
			return await index.DeleteAsync(server);
		}

		public Task<IReadOnlyList<string>> ListAsync() => index.ListAsync();
	}

	private static async Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, string? input = null)
	{
		var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false };
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(start)!;
			if (input is not null)
			{
				await process.StandardInput.WriteAsync(input);
				process.StandardInput.Close();
			}

			var output = await process.StandardOutput.ReadToEndAsync();
			await process.WaitForExitAsync();
			return (process.ExitCode, output);
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return (-1, "");
		}
	}

	private sealed class MacKeychain : IKeychain
	{
		public async Task<string?> ReadAsync(string server) =>
			await RunAsync("security", ["find-generic-password", "-s", Service, "-a", server, "-w"]) is (0, var output) ? output.TrimEnd('\n') : null;

		public async Task<bool> WriteAsync(string server, string secret) =>
			(await RunAsync("security", ["add-generic-password", "-U", "-s", Service, "-a", server, "-w", secret])).ExitCode == 0;

		public Task DeleteAsync(string server) => RunAsync("security", ["delete-generic-password", "-s", Service, "-a", server]);
	}

	private sealed class SecretService : IKeychain
	{
		public async Task<string?> ReadAsync(string server) =>
			await RunAsync("secret-tool", ["lookup", "service", Service, "server", server]) is (0, { Length: > 0 } output) ? output.TrimEnd('\n') : null;

		public async Task<bool> WriteAsync(string server, string secret) =>
			(await RunAsync("secret-tool", ["store", "--label", $"Bassia: {server}", "service", Service, "server", server], secret)).ExitCode == 0;

		public Task DeleteAsync(string server) => RunAsync("secret-tool", ["clear", "service", Service, "server", server]);
	}

	/// <summary>The Windows Credential Manager (generic credentials named <c>bassia:&lt;server&gt;</c>).</summary>
	private sealed class WindowsCredentials : IKeychain
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct Credential
		{
			public int Flags;
			public int Type;
			public string TargetName;
			public string? Comment;
			public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
			public int CredentialBlobSize;
			public IntPtr CredentialBlob;
			public int Persist;
			public int AttributeCount;
			public IntPtr Attributes;
			public string? TargetAlias;
			public string? UserName;
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CredWriteW(ref Credential credential, int flags);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool CredDeleteW(string target, int type, int flags);

		[DllImport("advapi32.dll")]
		private static extern void CredFree(IntPtr buffer);

		private const int Generic = 1;

		private static string Target(string server) => $"{Service}:{server}";

		public Task<string?> ReadAsync(string server)
		{
			if (!CredReadW(Target(server), Generic, 0, out var pointer))
			{
				return Task.FromResult<string?>(null);
			}

			try
			{
				var credential = Marshal.PtrToStructure<Credential>(pointer);
				return Task.FromResult<string?>(Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2));
			}
			finally
			{
				CredFree(pointer);
			}
		}

		public Task<bool> WriteAsync(string server, string secret)
		{
			var blob = Marshal.StringToCoTaskMemUni(secret);
			try
			{
				var credential = new Credential
				{
					Type = Generic,
					TargetName = Target(server),
					CredentialBlobSize = secret.Length * 2,
					CredentialBlob = blob,
					Persist = 2,
					UserName = Environment.UserName
				};
				return Task.FromResult(CredWriteW(ref credential, 0));
			}
			finally
			{
				Marshal.FreeCoTaskMem(blob);
			}
		}

		public Task DeleteAsync(string server)
		{
			CredDeleteW(Target(server), Generic, 0);
			return Task.CompletedTask;
		}
	}
}

/// <summary>The OAuth/OIDC exchanges <c>bassia login</c> makes with an identity provider.</summary>
internal static class Oidc
{
	/// <summary>The issuer's authorization and token endpoints, from its discovery document.</summary>
	public static async Task<(string Authorize, string Token)> DiscoverAsync(HttpClient http, string issuer)
	{
		var document = await http.GetFromJsonAsync<JsonElement>(issuer.TrimEnd('/') + "/.well-known/openid-configuration");
		return (document.GetProperty("authorization_endpoint").GetString()!, document.GetProperty("token_endpoint").GetString()!);
	}

	/// <summary>Posts a token request and keeps what it returns in the credential.</summary>
	public static async Task<ServerCredential> TokenAsync(HttpClient http, string endpoint, IReadOnlyDictionary<string, string> form, ServerCredential credential)
	{
		using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form.Where(pair => pair.Value.Length > 0)));
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		if (!response.IsSuccessStatusCode || !body.TryGetProperty("access_token", out var access))
		{
			throw new MonorepoException($"The identity provider refused the token request: {(body.TryGetProperty("error_description", out var why) ? why : body)}.");
		}

		return credential with
		{
			AccessToken = access.GetString(),
			RefreshToken = body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : credential.RefreshToken,
			Expires = DateTimeOffset.UtcNow.AddSeconds(body.TryGetProperty("expires_in", out var expires) ? expires.GetInt64() : 300),
			TokenEndpoint = endpoint
		};
	}
}
