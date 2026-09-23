using Bassia.CliCommands.Agent;
using Bassia.Ui;

namespace Bassia.Tests.Ui;

public class RunSupervisorTests
{
	private static AgentRunOutcome Outcome(string status) =>
		new(status == "completed", $"run {status}",
			new RunMetadata { RunId = RunMetadata.NewRunId(), Status = status, Created = RunMetadata.Timestamp(), Select = "app@v0", Command = "echo hi", WorkspacePath = "w" },
			null!, []);

	[Fact]
	public async Task Start_ReturnsBeforeTheRunFinishesAndTheCardIsLiveMeanwhile()
	{
		var release = new TaskCompletionSource();
		using var supervisor = new RunSupervisor(async (_, _, _) =>
		{
			await release.Task;
			return Outcome("completed");
		});

		var key = supervisor.Start("app@v0", "echo hi");

		var live = Assert.Single(supervisor.Cards());
		Assert.Equal(key, live.Key);
		Assert.True(live.IsLive);
		Assert.Equal(["app"], live.Components);
		Assert.Equal(1, supervisor.LiveCount);

		release.SetResult();
		await supervisor.WhenAllSettledAsync();

		var settled = Assert.Single(supervisor.Cards());
		Assert.False(settled.IsLive);
		Assert.Equal(AgentRunPhase.Completed, settled.Phase);
		Assert.True(supervisor.ConsumeSettled());
		Assert.False(supervisor.ConsumeSettled());
	}

	[Fact]
	public async Task Steps_AndOutput_LandOnTheCardWhileTheRunIsGoing()
	{
		var release = new TaskCompletionSource();
		using var supervisor = new RunSupervisor(async (_, _, context) =>
		{
			context.OnStep!(new AgentRunStep("agent-run-" + new string('a', 32), AgentRunPhase.Agent, "running the agent"));
			context.OnOutput!("first line");
			context.OnOutput!("second line");
			await release.Task;
			return Outcome("completed");
		});

		var key = supervisor.Start("app@v0,lib@v1", "echo hi");
		await WaitForAsync(() => supervisor.OutputOf(key).Count == 2);

		var card = Assert.Single(supervisor.Cards());
		Assert.Equal(AgentRunPhase.Agent, card.Phase);
		Assert.Equal("running the agent", card.Message);
		Assert.Equal("second line", card.LastOutput);
		Assert.Equal(["app", "lib"], card.Components);
		Assert.Equal(["first line", "second line"], supervisor.OutputOf(key));

		release.SetResult();
		await supervisor.WhenAllSettledAsync();
	}

	[Fact]
	public async Task Cancel_SignalsTheRunsTokenAndTheCardEndsCancelled()
	{
		using var supervisor = new RunSupervisor(async (_, _, context) =>
		{
			await Task.Delay(Timeout.Infinite, context.Cancellation);
			return Outcome("completed");
		});

		var key = supervisor.Start("app@v0", "sleep");
		Assert.True(supervisor.Cancel(key));
		await supervisor.WhenAllSettledAsync();

		var card = Assert.Single(supervisor.Cards());
		Assert.Equal(AgentRunPhase.Cancelled, card.Phase);
		Assert.False(card.IsLive);
		// A run that has already ended cannot be cancelled again.
		Assert.False(supervisor.Cancel(key));
	}

	[Fact]
	public async Task AFailingRun_EndsAsFailedWithItsMessageInsteadOfThrowingToTheFrontend()
	{
		using var supervisor = new RunSupervisor((_, _, _) => throw new AgentException("no annotated tag"));

		supervisor.Start("app@v0", "echo hi");
		await supervisor.WhenAllSettledAsync();

		var card = Assert.Single(supervisor.Cards());
		Assert.Equal(AgentRunPhase.Failed, card.Phase);
		Assert.Equal("no annotated tag", card.Message);
	}

	[Fact]
	public async Task OutputTail_IsCappedSoALoudAgentCannotGrowWithoutBound()
	{
		using var supervisor = new RunSupervisor((_, _, context) =>
		{
			for (var i = 0; i < RunSupervisor.OutputTailLength + 50; i++)
			{
				context.OnOutput!($"line {i}");
			}

			return Task.FromResult(Outcome("completed"));
		});

		var key = supervisor.Start("app@v0", "echo hi");
		await supervisor.WhenAllSettledAsync();

		var output = supervisor.OutputOf(key);
		Assert.Equal(RunSupervisor.OutputTailLength, output.Count);
		Assert.Equal($"line {RunSupervisor.OutputTailLength + 49}", output[^1]);
	}

	[Fact]
	public async Task Cards_AreNewestFirst()
	{
		using var supervisor = new RunSupervisor((select, _, _) => Task.FromResult(Outcome("completed")));

		supervisor.Start("first@v0", "echo hi");
		supervisor.Start("second@v0", "echo hi");
		await supervisor.WhenAllSettledAsync();

		Assert.Equal(["second@v0", "first@v0"], supervisor.Cards().Select(card => card.Select));
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "the condition was not reached in time");
			await Task.Delay(20);
		}
	}
}
