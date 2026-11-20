using System.Text.RegularExpressions;

// A stand-in for 'claude -p': the scenarios set agent.command to this program, so 'bassia agent start -prompt <text>'
// runs it in the workspace folder with the prompt as its last argument (options such as --model come before it). It
// understands the sentences the scenarios use, one per line or one after another ("... Delete the file a.txt. Create ..."), and the same prompts work for the real
// Claude Code (BASSIA_SCENARIO_CLAUDE=1):
//
//   Create a file named <path> containing exactly the text: <text>
//   Append the line <text> to the file <path>
//   Replace <old> with <new> in the file <path>
//   Delete the file <path>
//   Fail with exit code <n>
//   Sleep for <n> seconds
//
// Anything else is printed and ignored. Paths are relative to the workspace folder.
var prompt = args.Length > 0 ? args[^1] : "";
Console.WriteLine($"stand-in agent in {Environment.CurrentDirectory}");
Console.WriteLine($"run {Environment.GetEnvironmentVariable("BASSIA_RUN_ID")} in workspace {Environment.GetEnvironmentVariable("BASSIA_WORKSPACE")}");
if (Array.IndexOf(args, "--model") is var modelAt and >= 0 && modelAt + 1 < args.Length)
{
	Console.WriteLine($"model: {args[modelAt + 1]}");
}

foreach (var raw in Regex.Split(prompt, @"\r?\n|(?<=\.)\s+(?=(?:Create|Append|Replace|Delete|Fail|Sleep) )"))
{
	var line = raw.Trim();
	if (line.Length == 0)
	{
		continue;
	}

	Match match;
	if ((match = Regex.Match(line, @"^Create a file named (?<path>\S+) containing exactly the text: (?<text>.*?)$")).Success)
	{
		var path = Full(match.Groups["path"].Value);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, match.Groups["text"].Value + "\n");
		Console.WriteLine($"created {match.Groups["path"].Value}");
	}
	else if ((match = Regex.Match(line, @"^Append the line (?<text>.*) to the file (?<path>\S+?)\.?$")).Success)
	{
		File.AppendAllText(Full(match.Groups["path"].Value), match.Groups["text"].Value + "\n");
		Console.WriteLine($"appended to {match.Groups["path"].Value}");
	}
	else if ((match = Regex.Match(line, @"^Replace (?<old>.+) with (?<new>.+) in the file (?<path>\S+?)\.?$")).Success)
	{
		var path = Full(match.Groups["path"].Value);
		File.WriteAllText(path, File.ReadAllText(path).Replace(match.Groups["old"].Value, match.Groups["new"].Value));
		Console.WriteLine($"edited {match.Groups["path"].Value}");
	}
	else if ((match = Regex.Match(line, @"^Delete the file (?<path>\S+?)\.?$")).Success)
	{
		File.Delete(Full(match.Groups["path"].Value));
		Console.WriteLine($"deleted {match.Groups["path"].Value}");
	}
	else if ((match = Regex.Match(line, @"^Fail with exit code (?<code>\d+)\.?$")).Success)
	{
		Console.Error.WriteLine("failing as asked");
		return int.Parse(match.Groups["code"].Value);
	}
	else if ((match = Regex.Match(line, @"^Sleep for (?<seconds>\d+) seconds?\.?$")).Success)
	{
		Thread.Sleep(TimeSpan.FromSeconds(int.Parse(match.Groups["seconds"].Value)));
	}
	else
	{
		Console.WriteLine($"(not understood) {line}");
	}
}

return 0;

static string Full(string relative) => Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, relative));
