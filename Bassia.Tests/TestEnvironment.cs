using System.Text;

namespace Bassia.Tests;

internal sealed class TempDirectory : IDisposable
{
	public string Path { get; }

	public TempDirectory()
	{
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bassia-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Path, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup; leftover temp folders don't affect other tests.
		}
	}
}

internal static class TestEnvironment
{
	public static Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args) =>
		RunInDirectoryAsync(Environment.CurrentDirectory, args);

	public static async Task<(int ExitCode, string Output, string Error)> RunInDirectoryAsync(string directory, params string[] args)
	{
		var originalDirectory = Environment.CurrentDirectory;
		var originalOut = Console.Out;
		var originalError = Console.Error;

		var outWriter = new StringWriter(new StringBuilder());
		var errorWriter = new StringWriter(new StringBuilder());

		Directory.SetCurrentDirectory(directory);
		Console.SetOut(outWriter);
		Console.SetError(errorWriter);

		try
		{
			var exitCode = await ProgramCli.RunAsync(args);
			return (exitCode, outWriter.ToString(), errorWriter.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Directory.SetCurrentDirectory(originalDirectory);
		}
	}
}
