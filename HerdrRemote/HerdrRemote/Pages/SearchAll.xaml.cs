using System.Globalization;
using HerdrRemote.Remote;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Search every live terminal only when the user submits a query. Use read-only terminal.read for the screen
// and 3000 history lines, with at most four concurrent reads; never attach or resize. Insert each result
// as it arrives in Arranged order. Tapping a group opens that pane's Find with the same query.
// Do no work before submission or off screen; leaving cancels pending reads.
public partial class SearchAll : ContentPage
{
	private const int HistoryLines = 3000;
	private const int MaxInFlight = 4;
	private const int ShownLines = 3;

	// One terminal's answer: its matches counted, its newest lines cut around the match, and whether the read was whole.
	private sealed record Hit(string Address, string Title, int Count, List<string> Lines, bool Complete);

	private CancellationTokenSource? _search;
	private bool _opening;
	private bool _focused;

	// The shown groups' addresses in list order, beside the views (a group arriving later takes its place among them).
	private readonly List<(string Address, View View)> _groups = [];

	public SearchAll()
	{
		InitializeComponent();
		App.ApplyKeyboardSafeArea(this);
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		Header.TitleText = i18n.Translate("Search all panes");
		Field.Placeholder = i18n.Translate("Search all panes");
		if (!_focused)
		{
			// After the push animation: a field focused while the page is still arriving takes no focus.
			_focused = true;
			Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () => Field.Focus());
		}
	}

	protected override void OnDisappearing()
	{
		_search?.Cancel();
		Header.DetachReading();
		base.OnDisappearing();
	}

	private void Back_Clicked(object? sender, EventArgs e) => _ = App.GoToAsync("..");

	private void Field_Completed(object? sender, EventArgs e) => _ = SearchAsync((Field.Text ?? "").Trim());

	private async Task SearchAsync(string query)
	{
		_search?.Cancel();
		Results.Clear();
		_groups.Clear();
		if (query.Length < 2) { return; }
		CancellationTokenSource source = _search = new();
		CancellationToken ct = source.Token;
		CoreApi? api = App.Herdr.Api;
		List<AgentTerminal> terminals = App.Herdr.State == SessionState.Connected && api is not null
			? [.. App.Herdr.Agents.Arranged.SelectMany(space => space.Terminals).DistinctBy(terminal => terminal.Address)] : [];
		int shown = 0;
		using SemaphoreSlim gate = new(MaxInFlight);
		try
		{
			await Task.WhenAll(terminals.Select(async terminal =>
			{
				Hit? hit;
				await gate.WaitAsync(ct).ConfigureAwait(false);
				try { hit = await ReadAsync(api!, terminal, query, ct).ConfigureAwait(false); }
				catch (OperationCanceledException) { throw; }
				// One terminal failing to read never stops the others.
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[SearchAll] read {terminal.Address} failed: {ex.Message}");
					hit = null;
				}
				finally { gate.Release(); }
				if (hit is null) { return; }
				await Dispatcher.DispatchAsync(() =>
				{
					if (ct.IsCancellationRequested) { return; }
					shown++;
					Insert(terminals, hit, query);
				}).ConfigureAwait(false);
			})).ConfigureAwait(false);
		}
		catch (OperationCanceledException) { return; }
		await Dispatcher.DispatchAsync(() =>
		{
			if (ct.IsCancellationRequested || shown > 0) { return; }
			Results.Add(new Label { Style = App.Res<Style>("Sec.Note"), Text = i18n.Translate("No matches") });
		});
	}

	// Find case-insensitive literal matches off the UI thread.
	private static async Task<Hit?> ReadAsync(CoreApi api, AgentTerminal terminal, string query, CancellationToken ct)
	{
		CoreRead read = await api.ReadAsync(terminal.Address, HistoryLines, ct).ConfigureAwait(false);
		int count = 0;
		List<string> lines = [];
		foreach (string raw in read.Text.Split('\n'))
		{
			string line = raw.TrimEnd();
			int found = CountIn(line, query);
			if (found == 0) { continue; }
			count += found;
			lines.Add(line);
		}
		if (count == 0) { return null; }
		string title = terminal.Space.Length > 0 ? $"{terminal.Name} · {terminal.Space}" : terminal.Name;
		return new Hit(terminal.Address, title, count, lines.Count > ShownLines ? lines[^ShownLines..] : lines, read.Complete);
	}

	private static int CountIn(string line, string query)
	{
		int count = 0;
		for (int at = line.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase)) { count++; }
		return count;
	}

	// The group at its place in the Arranged order among the ones already shown.
	private void Insert(List<AgentTerminal> order, Hit hit, string query)
	{
		int rank = order.FindIndex(terminal => terminal.Address == hit.Address);
		int at = _groups.Count(group => order.FindIndex(terminal => terminal.Address == group.Address) < rank);
		View view = BuildGroup(hit, query);
		_groups.Insert(at, (hit.Address, view));
		Results.Insert(at, view);
	}

	private View BuildGroup(Hit hit, string query)
	{
		VerticalStackLayout group = new() { Padding = new Thickness(16, 10), Spacing = 4 };
		Grid head = new() { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 8 };
		head.Add(new Label { Style = App.Res<Style>("Row.Name"), Text = hit.Title, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 });
		head.Add(new Label { Style = App.Res<Style>("Grp.Count"), Text = hit.Count.ToString(CultureInfo.CurrentCulture), VerticalOptions = LayoutOptions.Center }, 1);
		group.Add(head);
		if (!hit.Complete) { group.Add(new Label { Style = App.Res<Style>("Sec.Note"), Text = i18n.Translate("Screen only"), HorizontalTextAlignment = TextAlignment.Start, Padding = 0 }); }
		foreach (string line in hit.Lines) { group.Add(LineLabel(line, query)); }
		group.Add(new BoxView { Style = App.Res<Style>("Lst.Divider"), Margin = new Thickness(-16, 6, -16, -10) });
		SemanticProperties.SetDescription(group, $"{hit.Title}, {hit.Count.ToString(CultureInfo.CurrentCulture)}");
		TapGestureRecognizer tap = new();
		tap.Tapped += async (_, _) => await OpenAsync(hit.Address, query);
		group.GestureRecognizers.Add(tap);
		return group;
	}

	// A matching line cut around its first match, every match in the cut emphasized.
	private static Label LineLabel(string line, string query)
	{
		string text = line.TrimStart();
		int first = Math.Max(0, text.IndexOf(query, StringComparison.OrdinalIgnoreCase));
		int start = Math.Max(0, first - 30);
		int end = Math.Min(text.Length, first + query.Length + 70);
		FormattedString formatted = new();
		void Plain(string value) { if (value.Length > 0) { formatted.Spans.Add(new Span { Text = value }); } }
		if (start > 0) { Plain("…"); }
		string cut = text[start..end];
		int from = 0;
		for (int at = cut.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = cut.IndexOf(query, from, StringComparison.OrdinalIgnoreCase))
		{
			Plain(cut[from..at]);
			Span hit = new() { Text = cut.Substring(at, query.Length), FontFamily = "GeistMonoMedium" };
			hit.SetAppThemeColor(Span.TextColorProperty, App.Res<Color>("Acc"), App.Res<Color>("AccDark"));
			formatted.Spans.Add(hit);
			from = at + query.Length;
		}
		Plain(cut[from..]);
		if (end < text.Length) { Plain("…"); }
		return new Label { Style = App.Res<Style>("Search.Line"), FormattedText = formatted };
	}

	private async Task OpenAsync(string address, string query)
	{
		if (_opening) { return; }
		_opening = true;
		try
		{
			try { await PopupsHandle.HideKeyboardAsync(); }
			catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SearchAll] hide keyboard unexpected: {ex}"); }
			await App.OpenPaneAsync(address, query);
		}
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SearchAll] open pane unexpected: {ex}"); }
		finally { _opening = false; }
	}
}
