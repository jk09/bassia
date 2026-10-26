namespace Bassia.Tests.Core;

using Bassia.Core;
using Bassia.Storage;
using Bassia.Storage.Memory;
using static MemoryMonorepo;

/// <summary>The user registry and attribution through the storage contract, on the memory storage.</summary>
public class UserTests
{
	private static readonly UserDefinition Jozef = new("jk", "Jozef Kosik", "jozef@example.com", "slack @jozef");

	[Theory]
	[InlineData("", "Jozef", "j@example.com", "not a valid user id")]
	[InlineData("a b", "Jozef", "j@example.com", "not a valid user id")]
	[InlineData("jk", "", "j@example.com", "needs a name")]
	[InlineData("jk", "Jozef <x>", "j@example.com", "needs a name")]
	[InlineData("jk", "Jozef", "not-an-email", "needs an email")]
	public void InvalidUser_IsRejected(string id, string name, string email, string expected) =>
		Assert.Contains(expected, Assert.Throws<MonorepoException>(() => UserDirectory.Empty.Set(new UserDefinition(id, name, email))).Message);

	[Fact]
	public void Directory_KeepsIdsAndEmailsUnique_AndFindsByEmailWithoutCase()
	{
		var users = UserDirectory.Empty.Set(Jozef, adding: true);

		Assert.Contains("already registered", Assert.Throws<MonorepoException>(() => users.Set(Jozef, adding: true)).Message);
		Assert.Contains("already has the email", Assert.Throws<MonorepoException>(() => users.Set(new UserDefinition("other", "Other", "JOZEF@example.com"))).Message);
		Assert.Equal("jk", users.FindByEmail("Jozef@Example.com")!.Id);
		Assert.Equal("Jozef K.", users.Set(Jozef with { Name = "Jozef K." }).Require("jk").Name);
		Assert.True(users.Remove("jk").IsEmpty);
	}

	[Fact]
	public async Task MetaRepo_KeepsUsersAsRevisions()
	{
		var store = new MemoryMetaStore();
		var metaRepo = await MetaRepo.OpenAsync(store);

		await metaRepo.CommitAsync(new RegistryChange.SetUser(Jozef), "add jk");
		await metaRepo.CommitAsync(new RegistryChange.SetUser(new UserDefinition("ann", "Ann", "ann@example.com")), "add ann");
		await metaRepo.CommitAsync(new RegistryChange.RemoveUser("ann"), "remove ann");

		var reopened = await MetaRepo.OpenAsync(store);
		Assert.Equal(["jk"], reopened.Registry.Users.Users.Select(user => user.Id));
		Assert.Equal(3, store.Revisions.Count);
	}

	[Theory]
	[InlineData("claude -p --permission-mode acceptEdits", "claude")]
	[InlineData("\"C:\\Tools\\codex.exe\" run", "codex")]
	[InlineData("/usr/local/bin/aider --yes", "aider")]
	[InlineData("   ", null)]
	public void AgentOf_IsTheCommandsProgram(string command, string? agent) => Assert.Equal(agent, Actor.AgentOf(command));

	[Fact]
	public async Task Actor_IsTheAuthorOfWhatTheStoresWrite_AndARunIsTheUsersWorkViaTheAgent()
	{
		using var monorepo = new MemoryMonorepo();
		using (Actor.Use(new Actor(Jozef)))
		{
			await monorepo.AddAsync("lib", null, ("a.txt", "a\n"));
			var outcome = await monorepo.Runs(metadata =>
			{
				File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", "a.txt"), "changed\n");
				return 0;
			}).StartAsync("lib@v1", "claude -p \"change a\"", Quiet());

			var run = outcome.Metadata;
			var result = run.Components.Single();
			var store = monorepo.Graph.Store("lib");
			Assert.Equal("jk", run.User);
			Assert.Equal("jk", (await monorepo.Records.LoadLatestAsync(run.RunId))!.User);
			Assert.Equal("Jozef Kosik <jozef@example.com>", (await store.GetCommitAsync(result.ResultCommit!))!.Author);
			Assert.Contains("user = \"jk\"", (await store.GetCommitAsync(result.ResultCommit!))!.Message);
			Assert.Equal("claude via bassia <jozef@example.com>", (await store.GetTagAsync(result.ResultTag!))!.Tagger);
			Assert.Equal("Jozef Kosik <jozef@example.com>", (await store.GetTagAsync("v1"))!.Tagger);
		}

		Assert.Null(Actor.Current);
	}

	[Fact]
	public async Task AnIntegration_IsAttributedToTheUserWhoStartedIt()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("a.txt", "a\n"));
		var outcome = await monorepo.Runs(metadata =>
		{
			File.WriteAllText(Path.Combine(metadata.WorkspacePath, "lib", "b.txt"), "b\n");
			return 0;
		}).StartAsync("lib@v1", "agent \"add b\"", Quiet());
		var settings = new Bassia.Integration.IntegrationSettings
		{
			WorkspaceDir = monorepo.WorkspaceDir,
			RunResolver = _ => throw new InvalidOperationException("no resolver expected")
		};

		using (Actor.Use(new Actor(Jozef)))
		{
			var plan = await Bassia.Integration.IntegrationPlanner.PlanAsync(monorepo.Graph, settings, [outcome.Metadata]);
			var integration = await Bassia.Integration.IntegrationRunner.RunAsync(monorepo.Graph, settings, monorepo.Records, [outcome.Metadata], plan,
				"fake-resolver", new Bassia.Integration.IntegrationContext { OnProgress = _ => { } });

			var component = Assert.Single(integration.Record.Components);
			Assert.Equal("jk", integration.Record.User);
			Assert.Equal("jk", Bassia.Integration.IntegrationRecord.FromToml(integration.Record.ToToml()).User);
			Assert.Equal("Jozef Kosik <jozef@example.com>", (await monorepo.Graph.Store("lib").GetTagAsync(component.ResultTag!))!.Tagger);
			Assert.Equal("Jozef Kosik <jozef@example.com>", (await monorepo.Graph.Store("lib").GetCommitAsync(component.ResultCommit!))!.Author);
		}
	}

	[Fact]
	public async Task WithoutAnActor_NothingIsAttributed()
	{
		using var monorepo = new MemoryMonorepo();
		await monorepo.AddAsync("lib", null, ("a.txt", "a\n"));

		var store = monorepo.Graph.Store("lib");
		Assert.Null((await store.GetTagAsync("v1"))!.Tagger);
	}
}
