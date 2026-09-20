namespace Bassia;

using System.Diagnostics;

/// <summary>Directory links used to nest a component's checkout into another's: junctions on Windows, symlinks elsewhere.</summary>
internal static class DirectoryLinks
{
	public static async Task CreateAsync(string linkPath, string targetPath)
	{
		if (!OperatingSystem.IsWindows())
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return;
		}

		// Junctions (unlike symbolic links) need no elevation or developer mode on Windows.
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};

		process.Start();
		var error = process.StandardError.ReadToEndAsync();
		var output = process.StandardOutput.ReadToEndAsync();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
		{
			throw new IOException($"Could not create junction '{linkPath}' -> '{targetPath}': {(await error).Trim()} {(await output).Trim()}".Trim());
		}
	}

	public static bool IsLink(string path) =>
		Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

	/// <summary>Removes the link itself, never the target's contents.</summary>
	public static void Delete(string linkPath)
	{
		if (IsLink(linkPath))
		{
			Directory.Delete(linkPath, recursive: false);
		}
	}
}
