namespace Bassia;

/// <summary>
/// The output of a detached job: while it is attached, everything this process would print goes to the job's log
/// (which <c>run logs</c> / <c>integration logs</c> read while it grows), and the final result goes to the job's
/// result file, so a log never embeds a <c># bassia result</c> document of its own.
/// </summary>
internal sealed class JobOutput : IDisposable
{
	private readonly TextWriter originalOut = Console.Out;
	private readonly TextWriter originalError = Console.Error;
	private readonly StreamWriter file;
	private readonly TextWriter log;

	private JobOutput(string path)
	{
		// The registry names a job's files <id>.log and <id>.result.toml, side by side.
		ResultPath = (path.EndsWith(".log", StringComparison.Ordinal) ? path[..^4] : path) + ".result.toml";
		file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
		log = TextWriter.Synchronized(file);
		Console.SetOut(log);
		Console.SetError(log);
	}

	public static JobOutput Redirect(string logPath) => new(logPath);

	public string ResultPath { get; }

	/// <summary>Appends one line to the log.</summary>
	public void Line(string line) => log.WriteLine(line);

	/// <summary>Writes the job's result to <see cref="ResultPath"/> and a closing line to the log.</summary>
	public int WriteResult(bool ok, string command, string message, IReadOnlyDictionary<string, object?>? data = null)
	{
		Line($"bassia: {(ok ? "done" : "failed")}: {message}");
		using var result = new StreamWriter(ResultPath, append: false);
		Console.SetOut(result);
		Console.SetError(result);
		try
		{
			return ProgramCli.WriteResult(ok, command, message, data);
		}
		finally
		{
			Console.SetOut(log);
			Console.SetError(log);
		}
	}

	public void Dispose()
	{
		Console.SetOut(originalOut);
		Console.SetError(originalError);
		file.Dispose();
	}
}
