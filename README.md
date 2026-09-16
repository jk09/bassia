# MiniRepo

MiniRepo is a small command-line interface built on top of Git. It delegates repository storage, history, branching, and commit behavior to the installed Git executable instead of reimplementing Git internals.

## Requirements

- .NET SDK 10.0 or later
- Git available on `PATH`

## Run

From a Git repository:

```powershell
dotnet run -- --help
dotnet run -- status
dotnet run -- log
dotnet run -- branch
dotnet run -- commit -m "Describe the change"
```

MiniRepo passes arguments to Git without invoking a shell. This keeps commit messages and paths from being interpreted as shell commands.

## Build

```powershell
dotnet build
```

The current command surface is intentionally small. Future features can add workflow-specific behavior while continuing to use Git for repository compatibility.