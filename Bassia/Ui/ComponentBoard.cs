namespace Bassia.Ui;

using Bassia.Git;

/// <summary>
/// What the components wallboard shows about one component beyond its registration. The git-derived parts are
/// read once per refresh, not per repaint: the board animates, but a component's tags do not change four times
/// a second.
/// </summary>
internal sealed record ComponentStatus(ComponentDefinition Definition, bool HasRepo, int AnnotatedTags, int Branches, string? LatestTag, int RecordedRuns)
{
	public string Name => Definition.Name;

	/// <summary>The status of every registered component; <paramref name="runs"/> are the recorded runs to count.</summary>
	public static async Task<IReadOnlyList<ComponentStatus>> ReadAllAsync(Monorepo monorepo, IReadOnlyList<RunMetadata> runs)
	{
		var statuses = new List<ComponentStatus>();
		foreach (var component in monorepo.Components)
		{
			var recorded = runs.Count(run => run.Components.Any(entry => entry.Name == component.Name));
			var sourceDir = monorepo.SourceRepoDir(component.Name);
			if (!Directory.Exists(sourceDir))
			{
				statuses.Add(new ComponentStatus(component, HasRepo: false, 0, 0, null, recorded));
				continue;
			}

			try
			{
				var refs = await GitRef.ListAsync(GitClient.In(sourceDir));
				var tags = refs.Where(reference => reference.Kind == GitRefKind.AnnotatedTag).ToList();
				statuses.Add(new ComponentStatus(component, HasRepo: true, tags.Count,
					refs.Count(reference => reference.Kind == GitRefKind.Branch), tags.LastOrDefault()?.Name, recorded));
			}
			catch (GitException)
			{
				statuses.Add(new ComponentStatus(component, HasRepo: false, 0, 0, null, recorded));
			}
		}

		return statuses;
	}
}

/// <summary>
/// The components view: one rectangle per registered component, placed in layers (a component sits below
/// everything that references it) and connected by ASCII lines that follow <c>references</c>. It is the
/// dependency graph and the component list in one picture, which is what the frontend lands on.
/// </summary>
internal sealed class ComponentBoard
{
	private const int CardWidth = 30, CardHeight = 6, HorizontalGap = 3, VerticalGap = 3;

	private readonly IReadOnlyList<ComponentStatus> components;
	private readonly ComponentGraph graph;
	private readonly Dictionary<string, (int X, int Y)> positions = new(StringComparer.Ordinal);
	private readonly int height;

	public ComponentBoard(IReadOnlyList<ComponentStatus> components, ComponentGraph graph, int width)
	{
		this.components = components;
		this.graph = graph;

		var perRow = Math.Max(1, (width + HorizontalGap) / (CardWidth + HorizontalGap));
		var levels = graph.Levels();
		var order = new List<string>();
		var row = 0;

		// Each level becomes one board row, wrapped into further rows when it is wider than the terminal.
		foreach (var level in components.GroupBy(component => levels.TryGetValue(component.Name, out var value) ? value : 0).OrderBy(group => group.Key))
		{
			foreach (var chunk in level.OrderBy(component => component.Name, StringComparer.Ordinal).Chunk(perRow))
			{
				for (var column = 0; column < chunk.Length; column++)
				{
					positions[chunk[column].Name] = (column * (CardWidth + HorizontalGap), row * (CardHeight + VerticalGap));
					order.Add(chunk[column].Name);
				}

				row++;
			}
		}

		Order = order;
		Width = Math.Max(1, Math.Min(perRow, Math.Max(1, order.Count)) * (CardWidth + HorizontalGap) - HorizontalGap);
		height = Math.Max(1, row * (CardHeight + VerticalGap) - VerticalGap);
	}

	/// <summary>Component names in the order the board draws them, which is the order the arrow keys walk.</summary>
	public IReadOnlyList<string> Order { get; }

	public int Width { get; }

	/// <summary>The board as Spectre markup. <paramref name="activeRuns"/> is live state and may change every frame.</summary>
	public string Render(int selected, IReadOnlyDictionary<string, int> activeRuns)
	{
		if (components.Count == 0)
		{
			return "[grey](no components registered; run 'bassia component add -url <url>' first)[/]";
		}

		return Draw(selected, activeRuns).ToMarkup();
	}

	/// <summary>The board as plain ASCII, without a selection: the picture <c>bassia graph</c> prints.</summary>
	public string RenderAscii(IReadOnlyDictionary<string, int> activeRuns) =>
		components.Count == 0 ? "(no components registered)" : Draw(-1, activeRuns).ToAscii();

	private CharCanvas Draw(int selected, IReadOnlyDictionary<string, int> activeRuns)
	{
		var canvas = new CharCanvas(Width, height);

		// Edges first: a card is opaque, so a line that would cut through a rectangle disappears under it.
		foreach (var component in components)
		{
			DrawReferences(canvas, component.Definition);
		}

		for (var i = 0; i < Order.Count; i++)
		{
			var component = components.First(candidate => candidate.Name == Order[i]);
			activeRuns.TryGetValue(component.Name, out var active);
			DrawCard(canvas, component, i == selected, active);
		}

		return canvas;
	}

	private void DrawReferences(CharCanvas canvas, ComponentDefinition component)
	{
		var (fromX, fromY) = positions[component.Name];
		for (var i = 0; i < component.References.Count; i++)
		{
			var reference = component.References[i];
			if (!positions.TryGetValue(reference.Name, out var to) || to.Y <= fromY)
			{
				// Unregistered, or a cycle that put the target on the same or a higher row: the card text says so.
				continue;
			}

			var referrers = graph.ReferrersOf(reference.Name);
			var incoming = Math.Max(1, referrers.Count);
			var slot = Math.Max(0, referrers.ToList().FindIndex(referrer => referrer.Name == component.Name));

			canvas.ConnectDown(
				fromX + Anchor(i, component.References.Count), fromY + CardHeight - 1,
				to.X + Anchor(slot, incoming), to.Y,
				"grey");
		}
	}

	/// <summary>Column of the <paramref name="index"/>-th of <paramref name="count"/> edges attached to a card edge.</summary>
	private static int Anchor(int index, int count) => 1 + (index + 1) * (CardWidth - 2) / (count + 1);

	private void DrawCard(CharCanvas canvas, ComponentStatus component, bool selected, int activeRuns)
	{
		var (x, y) = positions[component.Name];
		var style = !component.HasRepo ? "red" : activeRuns > 0 ? "deepskyblue1" : "grey";
		canvas.Box(x, y, CardWidth, CardHeight, selected ? $"bold yellow" : style, (selected ? "▸ " : "") + component.Name);

		const int inner = CardWidth - 4;
		var text = x + 2;
		canvas.Text(text, y + 1, component.HasRepo
			? $"{component.AnnotatedTags} tags · {component.Branches} branches"
			: "no local repository", inner, component.HasRepo ? null : "red");
		canvas.Text(text, y + 2, "needs: " + Join(component.Definition.References.Select(Describe)), inner, "grey");
		canvas.Text(text, y + 3, "used by: " + Join(graph.ReferrersOf(component.Name).Select(referrer => referrer.Name)), inner, "grey");
		canvas.Text(text, y + 4, activeRuns > 0
			? $"● {activeRuns} running · {component.RecordedRuns} runs"
			: $"{component.RecordedRuns} runs · {component.LatestTag ?? "no tag"}",
			inner, activeRuns > 0 ? "deepskyblue1" : "grey");
	}

	private static string Describe(ComponentReference reference) =>
		reference.Path == reference.Name ? reference.Name : $"{reference.Name}@{reference.Path}";

	private static string Join(IEnumerable<string> values)
	{
		var joined = string.Join(", ", values);
		return joined.Length == 0 ? "-" : joined;
	}
}
