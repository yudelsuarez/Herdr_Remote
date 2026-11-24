using Languages;

namespace Popups;

// The choice sheet, the body of a bottom sheet: the result is the tapped option, null on Cancel. selected = the current option, drawn with a check.
public partial class ListPicker : ContentView
{
    internal const int RowHeight = 50;
    internal const int MaxRows = 10;   // more options scroll

    public sealed record Option(string Text, string Subtitle, bool Selected, bool Divider, int Height)
    {
        public bool HasSubtitle => Subtitle.Length > 0;
        // The row's spoken name: the text and the subtitle, with the selected state (the check glyph is not read).
        public string Spoken => HerdrRemote.Pages.ChipRow.Spoken(HasSubtitle ? $"{Text}, {Subtitle}" : Text, Selected)!;
    }

    private readonly Action<string?> _close;

    public ListPicker(List<string> items, string title, string? selected, Action<string?> close)
    {
        _close = close;
        InitializeComponent();

        Lbl_Title.Text = title;
        Btn_Cancel.Text = i18n.Translate("Cancel");
        List<Option> options = [.. items.Select((t, i) => new Option(t, string.Empty, t == selected, i < items.Count - 1, RowHeight))];
        ListPickerItems.HeightRequest = Math.Min(options.Count, MaxRows) * RowHeight;
        ListPickerItems.ItemsSource = options;
    }

    // The pick is handled and the selection cleared, so the row keeps no selected look; clearing raises this again with nothing selected.
    private void Item_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count == 0) { return; }
        Option option = (Option)e.CurrentSelection[0];
        ListPickerItems.SelectedItem = null;
        _close(option.Text);
    }

    private void Cancel_Clicked(object? sender, EventArgs e) => _close(null);
}
