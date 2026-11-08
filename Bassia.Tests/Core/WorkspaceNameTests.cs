namespace Bassia.Tests.Core;

using Bassia.Core.Model;
using Bassia.Core.Storage;
using Bassia.Core.Version;

/// <summary>Workspace names are unique among open workspaces (bassia-e67c9f0e).</summary>
public class WorkspaceNameTests
{
	[Fact]
	public async Task Create_RefusesTheNameOfAnOpenWorkspace_AndWritesNothing()
	{
		var repo = await TestRepo.CreateAsync();
		var first = await repo.Workspaces.CreateAsync("ws1", "ann");
		var error = await Assert.ThrowsAsync<WorkspaceNameTakenException>(() => repo.Workspaces.CreateAsync("ws1", "bob"));
		Assert.Equal($"A workspace named 'ws1' is already open ({first.Id}).", error.Message);
		Assert.Equal(first.Id, Assert.Single(await repo.Repo.ListWorkspacesAsync()).Id);
	}

	[Fact]
	public async Task Clone_RefusesTheNameOfAnOpenWorkspace()
	{
		var repo = await TestRepo.CreateAsync();
		var source = await repo.Workspaces.CreateAsync("source", "ann");
		await repo.Workspaces.CreateAsync("taken", "ann");
		await Assert.ThrowsAsync<WorkspaceNameTakenException>(() => repo.Workspaces.CloneAsync(source.Id, "taken", "ann"));
		await Assert.ThrowsAsync<WorkspaceNameTakenException>(() => repo.Workspaces.CloneAsync(source.Id, "source", "ann"));
		Assert.Equal(2, (await repo.Repo.ListWorkspacesAsync()).Count);
	}

	[Fact]
	public async Task Names_AreCaseSensitive()
	{
		var repo = await TestRepo.CreateAsync();
		await repo.Workspaces.CreateAsync("ws1", "ann");
		await repo.Workspaces.CreateAsync("WS1", "ann");
		Assert.Equal(2, (await repo.Repo.ListWorkspacesAsync()).Count);
	}

	[Fact]
	public async Task LandingOrAbandoning_FreesTheName()
	{
		var repo = await TestRepo.CreateAsync();
		var landed = await repo.WorkspaceAsync("ws1", new Dictionary<string, string?> { ["a.txt"] = "a\n" });
		Assert.Equal(LandOutcome.Landed, (await repo.LandAsync(landed)).Outcome);
		var second = await repo.Workspaces.CreateAsync("ws1", "ann");
		await repo.Workspaces.SetStatusAsync(second.Id, WorkspaceStatus.Abandoned, "abandon", "ann");
		var third = await repo.Workspaces.CreateAsync("ws1", "ann");
		Assert.Equal(third.Id, (await repo.Repo.ResolveWorkspaceAsync("ws1")).Id);
		Assert.Equal(third.Id, (await repo.Repo.OpenWorkspaceNamedAsync("ws1"))!.Id);
	}

	[Fact]
	public async Task ConcurrentCreates_OfOneName_LeaveExactlyOneWorkspace()
	{
		var repo = await TestRepo.CreateAsync();
		var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
		{
			try
			{
				return await repo.Workspaces.CreateAsync("race", "agent-" + index);
			}
			catch (WorkspaceNameTakenException)
			{
				return null;
			}
		})).ToList();
		var created = (await Task.WhenAll(attempts)).OfType<WorkspaceState>().ToList();
		var winner = Assert.Single(created);
		Assert.Equal(winner.Id, Assert.Single(await repo.Repo.ListWorkspacesAsync()).Id);
	}

	[Fact]
	public async Task LegacyDuplicates_KeepWorkingById_AndKeepTheirName()
	{
		var repo = await TestRepo.CreateAsync();
		// Two open workspaces of one name, as clients before name reservations could leave them: no name head.
		var a = await repo.Workspaces.CreateAsync("dup", "ann");
		await repo.Views.DeleteHeadAsync(HeadName.WorkspaceName("dup"), a.Head);
		var b = await repo.Workspaces.CreateAsync("dup-b", "ann");
		var main = await repo.Repo.GetMainAsync();
		var legacy = await repo.Repo.CommitWorkspaceAsync(null,
			new WorkspaceRecord(WorkspaceService.NewId(), "dup", null, main.CommitId, main.Seq, b.Record.Working, WorkspaceStatus.Open, DateTimeOffset.UtcNow, "ann"),
			"legacy", "ann", null);

		await Assert.ThrowsAsync<WorkspaceNameTakenException>(() => repo.Workspaces.CreateAsync("dup", "bob"));
		Assert.Equal(a.Id, (await repo.Repo.ResolveWorkspaceAsync(a.Id)).Id);
		Assert.Equal(legacy.Id, (await repo.Repo.ResolveWorkspaceAsync(legacy.Id)).Id);
		await Assert.ThrowsAsync<VersionException>(() => repo.Repo.ResolveWorkspaceAsync("dup"));

		await repo.Workspaces.SetStatusAsync(a.Id, WorkspaceStatus.Abandoned, "abandon", "ann");
		await Assert.ThrowsAsync<WorkspaceNameTakenException>(() => repo.Workspaces.CreateAsync("dup", "bob"));
		await repo.Workspaces.SetStatusAsync(legacy.Id, WorkspaceStatus.Abandoned, "abandon", "ann");
		await repo.Workspaces.CreateAsync("dup", "bob");
	}

	[Fact]
	public async Task FreeWorkspaceName_AddsTheNextFreeSuffix()
	{
		var repo = await TestRepo.CreateAsync();
		Assert.Equal("restore-3", await repo.Repo.FreeWorkspaceNameAsync("restore-3"));
		await repo.Workspaces.CreateAsync("restore-3", "ann");
		Assert.Equal("restore-3-2", await repo.Repo.FreeWorkspaceNameAsync("restore-3"));
		await repo.Workspaces.CreateAsync("restore-3-2", "ann");
		Assert.Equal("restore-3-3", await repo.Repo.FreeWorkspaceNameAsync("restore-3"));
	}
}
