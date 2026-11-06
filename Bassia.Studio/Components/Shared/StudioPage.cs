namespace Bassia.Studio.Components.Shared;

using Microsoft.AspNetCore.Components;

/// <summary>
/// A page that acts: runs an action, shows its outcome (or the expected failure) as a banner, and reloads what it shows.
/// Unexpected failures still propagate (to the error page and the log).
/// </summary>
public abstract class StudioPage : ComponentBase, IDisposable
{
	[Inject] protected MonorepoWatch Watch { get; set; } = default!;

	private int refreshing;

	/// <summary>Whether the page reloads by itself when the monorepo changes (landings, workspaces, the periodic tick).</summary>
	protected virtual bool LiveUpdates => false;

	/// <summary>Whether a live reload should wait (the user is in the middle of an edit on the page).</summary>
	protected virtual bool Editing => false;

	protected override void OnInitialized()
	{
		if (LiveUpdates)
		{
			Watch.Changed += OnMonorepoChanged;
		}
	}

	private void OnMonorepoChanged(Bassia.Stores.Api.WatchEvent change)
	{
		// One reload at a time, at most once a second; none while the user edits or an action runs.
		if (Editing || Busy || Interlocked.Exchange(ref refreshing, 1) == 1)
		{
			return;
		}

		_ = InvokeAsync(async () =>
		{
			try
			{
				await LoadAsync();
				StateHasChanged();
			}
			catch (Exception ex) when (ErrorNotice.For(ex) is { } message)
			{
				Error = message;
				StateHasChanged();
			}
			finally
			{
				await Task.Delay(1000);
				Interlocked.Exchange(ref refreshing, 0);
			}
		});
	}

	public virtual void Dispose() => Watch.Changed -= OnMonorepoChanged;

	[Inject] protected MonorepoClient Client { get; set; } = default!;

	[Inject] protected StudioActions Actions { get; set; } = default!;

	[Inject] protected NavigationManager Navigation { get; set; } = default!;

	/// <summary>The outcome of the last action.</summary>
	protected string? Notice { get; set; }

	/// <summary>Why the last action or load failed.</summary>
	protected string? Error { get; set; }

	/// <summary>Whether an action is running (buttons are disabled meanwhile).</summary>
	protected bool Busy { get; private set; }

	protected StudioUser User => Actions.User;

	/// <summary>Loads what the page shows.</summary>
	protected abstract Task LoadAsync();

	protected override async Task OnParametersSetAsync()
	{
		Error = null;
		try
		{
			await LoadAsync();
		}
		catch (Exception ex) when (ErrorNotice.For(ex) is { } message)
		{
			Error = message;
		}
	}

	/// <summary>Runs an action; its result text becomes the notice; then the page reloads.</summary>
	protected async Task RunAsync(Func<Task<string?>> action, bool reload = true)
	{
		Busy = true;
		Notice = null;
		Error = null;
		try
		{
			Notice = await action();
			if (reload)
			{
				await LoadAsync();
			}
		}
		catch (Exception ex) when (ErrorNotice.For(ex) is { } message)
		{
			Error = message;
		}
		finally
		{
			Busy = false;
		}
	}
}
