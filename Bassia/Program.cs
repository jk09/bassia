namespace Bassia;

static class Program
{
	public static async Task<int> Main(string[] args)
	{
		return await ProgramCli.RunAsync(args);
	}
}
