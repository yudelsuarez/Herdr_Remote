using System.Diagnostics;
using HerdrRemote;
using HerdrRemote.Remote;
using Languages;

namespace Popups;

// Select a folder in use, browse from home, or filter a provider list; return null on dismissal.
public partial class ListSheet : ContentView
{
	internal const int MaxRows = 10;
	private const int RowHeight = 46;
	internal const int RowSubHeight = 58;

	// Key identifies the folder or provider item.
	public sealed record Item(string Key, string Text, string Subtitle, string Glyph, double GlyphTurn, string Trail, bool Dim, int Height)
	{
		public bool HasSubtitle => Subtitle.Length > 0;
		public string Spoken => string.Join(", ", new[] { Text, Subtitle }.Where(part => part.Length > 0));
	}

	// Pair each provider row with its case-insensitive search text.
	internal sealed record Filtered(Item Item, string Match);

	private readonly Action<string?> _close;
	private readonly string _home = "";
	private readonly bool _windows;
	private readonly IReadOnlyList<string> _folders = [];
	private readonly Func<string, Task<List<string>?>>? _list;
	// Browse uses _dir (empty in Folders mode); _load invalidates responses from earlier loads.
	private string _dir = "";
	private int _load;
	// Filter state includes rows, a count formatter (shown, total), and the empty-result note.
	private readonly bool _filtering;
	private IReadOnlyList<Filtered> _filtered = [];
	private Func<int, int, string> _count = (_, _) => "";
	private string _none = "";
	private BottomSheet? _host;
	private bool _closed, _hostShown, _focusPending;

	private ListSheet(string home, IReadOnlyList<string> folders, Func<string, Task<List<string>?>> list, Action<string?> close)
	{
		_close = result => { if (_closed) { return; } CloseWork(); close(result); };
		_home = home;
		_windows = RemotePath.IsWindows(home);
		_folders = folders;
		_list = list;
		InitializeComponent();
		ParentChanged += (_, _) => BindHost();
		Unloaded += (_, _) => CloseWork();
		Lbl_Title.Text = i18n.Translate("New space");
		Btn_Cancel.Text = i18n.Translate("Cancel");
		Lbl_Section.Text = i18n.Translate("Folders in use");
		Lbl_Browse.Text = i18n.Translate("Browse…");
		Btn_Open.Text = i18n.Translate("Open here");
		SemanticProperties.SetDescription(BrowseRow, Lbl_Browse.Text);
		SemanticProperties.SetDescription(Btn_Back, i18n.Translate("Back"));
		ShowFolders();
	}

	// Provider filter mode: the caller fills the rows synchronously with ShowFilter before hosting.
	private ListSheet(string title, Action<string?> close)
	{
		_close = result => { if (_closed) { return; } CloseWork(); close(result); };
		_filtering = true;
		InitializeComponent();
		ParentChanged += (_, _) => BindHost();
		Unloaded += (_, _) => CloseWork();
		Btn_Cancel.IsVisible = false;
		Lbl_Section.IsVisible = false;
		Divider.IsVisible = false;
		BrowseRow.IsVisible = false;
		Lbl_Title.Text = title;
	}

	private void BindHost()
	{
		Element? parent = Parent;
		while (parent is not null and not BottomSheet) { parent = parent.Parent; }
		if (ReferenceEquals(parent, _host)) { return; }
		if (_host is not null) { CloseWork(); }
		if (_closed || parent is not BottomSheet host) { return; }
		_host = host;
		host.Closing += Host_Closing;
		host.Shown += Host_Shown;
		if (host.IsClosing) { CloseWork(); }
	}

	private void Host_Closing(object? sender, EventArgs e) => CloseWork();
	private void Host_Shown(object? sender, EventArgs e) { _hostShown = true; FocusFilter(); }

	private void CloseWork()
	{
		if (_closed) { return; }
		_closed = true;
		_load++;
		_focusPending = false;
		if (_host is { } host) { host.Closing -= Host_Closing; host.Shown -= Host_Shown; }
	}

	private void FocusFilter()
	{
		if (_closed || !_focusPending || !_hostShown || _host is not { IsClosing: false, Parent: not null } || Parent is null || !FilterField.IsVisible) { return; }
		Dispatcher.Dispatch(() =>
		{
			if (_closed || !_focusPending || !_hostShown || _host is not { IsClosing: false, Parent: not null } || Parent is null || !FilterField.IsVisible) { return; }
			_focusPending = false;
			FilterEntry.Focus();
		});
	}

	// Opens the sheet; the result is the picked folder, null when closed without one.
	internal static Task<string?> PickFolderAsync(string home, IReadOnlyList<string> folders, Func<string, Task<List<string>?>> list, Func<ContentPage, Layout, bool>? CanInsert = null)
		=> PopupsHandle.PopBottomSheetAsync<string?>(close => new ListSheet(home, folders, list, close), CanInsert: CanInsert);

	// Opens a provider filter sheet titled `title`; opened receives it to be filled. The result is the picked row's Key,
	// null when closed without one.
	internal static Task<string?> PickFilteredAsync(string title, Action<ListSheet> opened, Func<ContentPage, Layout, bool>? CanInsert = null)
		=> PopupsHandle.PopBottomSheetAsync<string?>(close =>
		{
			ListSheet sheet = new(title, close);
			opened(sheet);
			return sheet;
		}, CanInsert: CanInsert);

	// Show the title, result count and filter field; none is an already-translated empty-result note.
	// Focus the field only after the host is shown.
	internal void ShowFilter(string title, string placeholder, IReadOnlyList<Filtered> rows, Func<int, int, string> count, string none)
	{
		if (_closed) { return; }
		Lbl_Title.Text = title;
		_filtered = rows;
		_count = count;
		_none = none;
		FilterField.IsVisible = true;
		FilterEntry.Placeholder = placeholder;
		SemanticProperties.SetDescription(FilterEntry, placeholder);
		Lbl_Count.IsVisible = true;
		Refilter();
		_focusPending = true;
		FocusFilter();
	}

	private void Refilter()
	{
		if (_closed) { return; }
		string f = (FilterEntry.Text ?? "").Trim().ToLowerInvariant();
		List<Item> shown = [.. _filtered.Where(row => f.Length == 0 || row.Match.ToLowerInvariant().Contains(f, StringComparison.Ordinal)).Select(row => row.Item)];
		Lbl_Count.Text = _count(shown.Count, _filtered.Count);
		SetItems(shown);
		ShowNote(shown.Count == 0 ? _none : null);
	}

	private void Filter_TextChanged(object? sender, TextChangedEventArgs e)
	{
		if (_filtering && FilterField.IsVisible) { Refilter(); }
	}

	// A path with home as "~".
	private static string Tilde(string path, string home) =>
		RemotePath.Tilde(path, home, RemotePath.IsWindows(home));

	private string Name(string path) => path == "/" ? "/" : RemotePath.Name(path, _windows);

	private void ShowFolders()
	{
		if (_closed) { return; }
		_load++;
		_dir = "";
		Btn_Back.IsVisible = false;
		Lbl_Path.IsVisible = false;
		Lbl_Note.IsVisible = false;
		Btn_Open.IsVisible = false;
		// With no folders in use, offer only Browse….
		Lbl_Section.IsVisible = _folders.Count > 0;
		Divider.IsVisible = _folders.Count > 0;
		BrowseRow.IsVisible = true;
		SetItems([.. _folders.Select(folder => new Item(folder, RemotePath.Name(folder, RemotePath.IsWindows(folder)) is { Length: > 0 } name ? name : folder, Tilde(folder, _home), "\uE928", 0, "", false, RowSubHeight))]);
	}

	private async void Browse(string dir)
	{
		if (_closed) { return; }
		int load = ++_load;
		_dir = dir;
		Btn_Back.IsVisible = true;
		Lbl_Section.IsVisible = false;
		Divider.IsVisible = false;
		BrowseRow.IsVisible = false;
		Btn_Open.IsVisible = true;
		Btn_Open.IsEnabled = false;
		Lbl_Path.FormattedText = Crumb(dir);
		Lbl_Path.IsVisible = true;
		ShowNote("Loading");
		List<Item> up = RemotePath.Parent(dir, _windows) is { } parent ? [new Item(parent, Name(parent), "", "\uE909", 180, "", true, RowHeight)] : [];
		SetItems(up);
		List<string>? names;
		try
		{
			names = await _list!(dir);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[ListSheet] listing {dir} failed: {ex.GetType().Name}: {ex.Message}");
			names = null;
		}
		if (_closed || load != _load) { return; }
		if (names is null)
		{
			ShowNote("Cannot list this folder");
			return;
		}
		ShowNote(names.Count == 0 ? "No folders inside." : null);
		Btn_Open.IsEnabled = true;
		SetItems([.. up, .. names.Select(name => new Item(RemotePath.Join(dir, name, _windows), name, "", "\uE928", 0, "\uE907", false, RowHeight))]);
	}

	// Show breadcrumb segments, such as "~ / Projects / app", with the last segment in the ink color.
	private FormattedString Crumb(string dir)
	{
		string shown = Tilde(dir, _home);
		List<string> parts = RemotePath.CrumbParts(shown, _windows);
		FormattedString crumb = new();
		for (int i = 0; i < parts.Count; i++)
		{
			string text = i == 0 || parts[i - 1] == "/" ? parts[i] : " / " + parts[i];
			crumb.Spans.Add(new Span { Text = text, Style = i == parts.Count - 1 ? App.Res<Style>("List.PathOn") : null });
		}
		return crumb;
	}

	private void ShowNote(string? key)
	{
		Lbl_Note.IsVisible = key is not null;
		// Provider filter notes are already translated; folder notes are translation keys.
		Lbl_Note.Text = key is null ? "" : _filtering ? key : i18n.Translate(key);
		Refit();
	}

	private void SetItems(List<Item> items)
	{
		Items.ItemsSource = items;
		Items.HeightRequest = items.Take(MaxRows).Sum(item => item.Height);
		Items.IsVisible = items.Count > 0;
		Refit();
	}

	// Refit the bottom sheet after its visible content changes.
	private void Refit()
	{
		Element? host = Parent;
		while (host is not null and not BottomSheet) { host = host.Parent; }
		(host as BottomSheet)?.Refit();
	}

	// Select a folder in Folders mode; in Browse, enter it or go up for the ".." row.
	private void Item_SelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_closed || e.CurrentSelection.Count == 0) { return; }
		Item item = (Item)e.CurrentSelection[0];
		Items.SelectedItem = null;
		if (_filtering || _dir.Length == 0) { _close(item.Key); }
		else { Browse(item.Key); }
	}

	private void Browse_Tapped(object? sender, TappedEventArgs e) => Browse(_home);

	private void Back_Clicked(object? sender, EventArgs e) => ShowFolders();

	private void Open_Clicked(object? sender, EventArgs e)
	{
		if (_dir.Length > 0) { _close(_dir); }
	}

	private void Cancel_Clicked(object? sender, EventArgs e) => _close(null);
}
