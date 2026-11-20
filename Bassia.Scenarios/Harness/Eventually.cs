namespace Bassia.Scenarios.Harness;

/// <summary>Waits for what happens in the background (detached runs, the server's reconciliation) instead of sleeping a fixed time.</summary>
public static class Eventually
{
	public static async Task AsyncTrue(Func<Task<bool>> condition, TimeSpan timeout, string? because = null)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (true)
		{
			if (await condition())
			{
				return;
			}

			if (DateTime.UtcNow > deadline)
			{
				Assert.Fail($"Not true within {timeout.TotalSeconds:F0} s{(because is null ? "" : ": " + because)}.");
			}

			await Task.Delay(250);
		}
	}

	/// <summary>Repeats <paramref name="read"/> until <paramref name="accept"/> holds; returns the accepted value.</summary>
	public static async Task<T> AsyncValue<T>(Func<Task<T>> read, Func<T, bool> accept, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (true)
		{
			var value = await read();
			if (accept(value) || DateTime.UtcNow > deadline)
			{
				return value;
			}

			await Task.Delay(250);
		}
	}
}
