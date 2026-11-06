namespace Bassia.Tests;

using Bassia.Agents;

/// <summary>Automatic snapshots while an agent runs (bassia-f162a7fd): once files settle, never while they change, never overlapping.</summary>
public sealed class AutoSnapshotTests : IDisposable
{
	private readonly TempDirectory temp = new();

	public void Dispose() => temp.Dispose();

	private static async Task UntilAsync(Func<bool> condition, string what)
	{
		var clock = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Timed out: {what}");
			await Task.Delay(20);
		}
	}

	[Fact]
	public async Task ItSnapshotsOnceTheFilesSettle_AndNotWhileNothingChanges()
	{
		var calls = 0;
		await using var auto = AutoSnapshot.Start(temp.Path, TimeSpan.FromMilliseconds(300), _ =>
		{
			Interlocked.Increment(ref calls);
			return Task.CompletedTask;
		})!;

		await File.WriteAllTextAsync(temp.Combine("a.txt"), "a");
		await File.WriteAllTextAsync(temp.Combine("b.txt"), "b");
		await UntilAsync(() => Volatile.Read(ref calls) == 1, "a snapshot after the first edits");
		await Task.Delay(800);
		Assert.Equal(1, Volatile.Read(ref calls));

		Directory.CreateDirectory(temp.Combine("src"));
		await File.WriteAllTextAsync(temp.Combine("src", "c.txt"), "c");
		await UntilAsync(() => Volatile.Read(ref calls) == 2, "a snapshot after more edits");
	}

	[Fact]
	public async Task ChangesUnderBassiaFolders_AreIgnored()
	{
		var calls = 0;
		await using var auto = AutoSnapshot.Start(temp.Path, TimeSpan.FromMilliseconds(200), _ =>
		{
			Interlocked.Increment(ref calls);
			return Task.CompletedTask;
		})!;
		Directory.CreateDirectory(temp.Combine(".bassia"));
		await File.WriteAllTextAsync(temp.Combine(".bassia", "state"), "x");
		await File.WriteAllTextAsync(temp.Combine(".bassia-workspace"), "x");
		await Task.Delay(800);
		Assert.Equal(0, Volatile.Read(ref calls));
	}

	[Fact]
	public async Task AFailedSnapshot_IsReported_AndTheNextChangeTriesAgain()
	{
		var calls = 0;
		var messages = new List<string>();
		await using var auto = AutoSnapshot.Start(temp.Path, TimeSpan.FromMilliseconds(200), _ =>
			Interlocked.Increment(ref calls) == 1 ? throw new IOException("server away") : Task.CompletedTask, message =>
			{
				lock (messages)
				{
					messages.Add(message);
				}
			})!;
		await File.WriteAllTextAsync(temp.Combine("a.txt"), "a");
		await UntilAsync(() => Volatile.Read(ref calls) == 1, "the first attempt");
		await File.WriteAllTextAsync(temp.Combine("a.txt"), "b");
		await UntilAsync(() => Volatile.Read(ref calls) == 2, "the second attempt");
		lock (messages)
		{
			Assert.Contains(messages, message => message.Contains("server away"));
		}
	}

	[Fact]
	public void ZeroQuiet_TurnsItOff() => Assert.Null(AutoSnapshot.Start(temp.Path, TimeSpan.Zero, _ => Task.CompletedTask));
}
