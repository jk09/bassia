namespace Bassia.Git;

internal sealed record GitResult(int ExitCode, string Output, string Error);
