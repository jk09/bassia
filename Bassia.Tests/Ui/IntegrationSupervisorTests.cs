using Bassia.Integration;
using Bassia.Ui;

namespace Bassia.Tests.Ui;

public class IntegrationSupervisorTests
{
	[Fact]
	public async Task Start_AnUnexpectedFailure_EndsTheCardAndTheNextIntegrationCanStart()
	{
		var calls = 0;
		using var supervisor = new IntegrationSupervisor((_, _, _, _) =>
		{
			calls++;
			return calls == 1
				? throw new InvalidOperationException("the resolver process could not be started")
				: Task.FromException<IntegrationOutcome>(new System.ComponentModel.Win32Exception("no shell"));
		});

		Assert.True(supervisor.Start([], [], "resolver"));
		await supervisor.WhenSettledAsync(); // must not throw into the frontend

		var card = supervisor.Current!;
		Assert.False(card.IsLive);
		Assert.Equal("the resolver process could not be started", card.Message);
		Assert.True(supervisor.ConsumeSettled());

		Assert.True(supervisor.Start([], [], "resolver"));
		await supervisor.WhenSettledAsync();
		Assert.False(supervisor.IsLive);
		Assert.Equal(2, calls);
	}
}
