using System.Collections.ObjectModel;
using HerdrRemote.Remote;
using Languages;
using Popups;
using Configuration = HerdrRemote.Remote.Shortcuts;

namespace HerdrRemote.Pages;

public partial class ShortcutsPage : ContentPage
{
	private readonly ObservableCollection<KeyBarEntry> _rows = [];
	private readonly BusyRunner _run;

	public ShortcutsPage()
	{
		InitializeComponent();
		_run = new(IsBusyBox);
		Items.ItemsSource = _rows;
		App.ApplyKeyboardSafeArea(this);
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		Header.TitleText = i18n.Translate("Shortcuts");
		Lbl_Corners.Text = i18n.Translate("D-pad corners");
		Lbl_Bar.Text = i18n.Translate("Key bar");
		Btn_Add.Text = "+ " + i18n.Translate("Add shortcut");
		Btn_Reset.Text = i18n.Translate("Reset to defaults");
		Show();
	}

	protected override void OnDisappearing()
	{
		Header.DetachReading();
		base.OnDisappearing();
	}

	private void Show()
	{
		Configuration current = Configuration.Current;
		if (!_rows.SequenceEqual(current.KeyBar))
		{
			_rows.Clear();
			foreach (KeyBarEntry entry in current.KeyBar) { _rows.Add(entry); }
		}
		if (AddCard.StrokeShape is Microsoft.Maui.Controls.Shapes.RoundRectangle shape)
		{
			shape.CornerRadius = _rows.Count == 0 ? new CornerRadius(App.Res<int>("R2")) : new CornerRadius(0, 0, App.Res<int>("R2"), App.Res<int>("R2"));
		}
		Dispatcher.Dispatch(RefreshRows);
		string[] positions = ["tl", "tr", "bl", "br"];
		Corners.SetItems(positions.Select((position, index) =>
		{
			Corner? corner = current.Corners.FirstOrDefault(item => item.Position == position);
			return new InfoRow(corner?.Action is null ? "" : corner.Glyph, ShortcutSheet.CornerName(position),
				ShortcutSheet.Summary(corner?.Action), index > 0, Chevron: true, Key: position);
		}).ToArray());
		Preview.Clear();
		for (int cell = 0; cell < 9; cell++)
		{
			int index = Array.IndexOf(new[] { 0, 2, 6, 8 }, cell);
			Corner? corner = index >= 0 ? current.Corners.FirstOrDefault(item => item.Position == positions[index]) : null;
			string text = index >= 0 ? corner?.Action is null ? "" : corner.Glyph.Length > 0 ? corner.Glyph : corner.Label[..Math.Min(3, corner.Label.Length)]
				: cell switch { 1 => "↑", 3 => "←", 4 => "⏎", 5 => "→", _ => "↓" };
			Button key = new() { Text = text, Style = App.Res<Style>(index >= 0 ? "TrayKey.On" : "TrayKey"),
				FontFamily = corner?.Glyph.Length > 0 ? "icons" : "GeistMonoMedium", WidthRequest = 34, HeightRequest = 34,
				MinimumWidthRequest = 34, MinimumHeightRequest = 34, Padding = 0, Opacity = index >= 0 ? 1 : 0.45 };
			Preview.Add(key, cell % 3, cell / 3);
		}
	}

	private async void Corners_Tapped(object? sender, TappedEventArgs e)
	{
		if (sender is not BindableObject { BindingContext: InfoRow row }) { return; }
		await _run.RunAsync("Shortcuts", async () => { await ShortcutSheet.PopAsync(position: row.Key); Show(); });
	}

	private async void Row_Tapped(object? sender, TappedEventArgs e)
	{
		if (sender is not BindableObject { BindingContext: KeyBarEntry entry }) { return; }
		await _run.RunAsync("Shortcuts", async () => { await ShortcutSheet.PopAsync(entry: entry); Show(); });
	}

	private async void Add_Clicked(object? sender, EventArgs e)
		=> await _run.RunAsync("Shortcuts", async () => { await ShortcutSheet.PopAsync(); Show(); });

	private async void Reset_Clicked(object? sender, EventArgs e)
	{
		await _run.RunAsync("Shortcuts", async () =>
		{
			string reset = i18n.Translate("Reset");
			if (await PopupsHandle.PopDisplayAsync(i18n.Translate("Reset shortcuts?"), "", [reset, i18n.Translate("Cancel")], Destructive: true) != reset) { return; }
			Configuration.Reset();
			Show();
		});
	}

	private void Items_ReorderCompleted(object? sender, EventArgs e) => SaveOrder();

	private void SaveOrder()
	{
		try
		{
			Configuration.Save(Configuration.Current with { KeyBar = [.. _rows] });
			Error.IsVisible = false;
		}
		catch (Exception)
		{
			Show();
			Error.Text = i18n.Translate("Something went wrong, please try again");
			Error.IsVisible = true;
		}
		RefreshRows();
	}

	private void RefreshRows()
	{
		foreach (IVisualTreeElement element in Items.GetVisualTreeDescendants())
		{
			if (element is Grid { BindingContext: KeyBarEntry } row && row.FindByName<Button>("Keycap") is not null) { Row_Bound(row, EventArgs.Empty); }
		}
	}

	private void Row_Bound(object? sender, EventArgs e)
	{
		if (sender is not Grid { BindingContext: KeyBarEntry entry } row || row.FindByName<Button>("Keycap") is not { } key) { return; }
		key.Style = App.Res<Style>(entry.Label is [>= '\uE000' and <= '\uF8FF'] ? "TrayKey.Icon" : "TrayKey");
		string summary = ShortcutSheet.Summary(entry.Action);
		row.FindByName<Label>("Summary").Text = summary;
		SemanticProperties.SetDescription(row, $"{(entry.Label is [>= '\uE000' and <= '\uF8FF'] ? summary : entry.Label)}, {summary}");
		SemanticProperties.SetHint(row, i18n.Translate("Drag to reorder"));
		int index = _rows.IndexOf(entry);
		if (row.Parent is Border { StrokeShape: Microsoft.Maui.Controls.Shapes.RoundRectangle shape })
		{
			shape.CornerRadius = index == 0 ? new CornerRadius(App.Res<int>("R2"), App.Res<int>("R2"), 0, 0) : new CornerRadius(0);
		}
#if IOS
		if (row.Handler?.PlatformView is UIKit.UIView view)
		{
			List<UIKit.UIAccessibilityCustomAction> actions = [];
			if (index > 0) { actions.Add(new(i18n.Translate("Move up"), new Func<UIKit.UIAccessibilityCustomAction, bool>(_ => Move(entry, -1)))); }
			if (index >= 0 && index < _rows.Count - 1) { actions.Add(new(i18n.Translate("Move down"), new Func<UIKit.UIAccessibilityCustomAction, bool>(_ => Move(entry, 1)))); }
			view.IsAccessibilityElement = true;
			view.AccessibilityCustomActions = [.. actions];
		}
#elif ANDROID
		if (row.Handler?.PlatformView is Android.Views.View view)
		{
			if (row.Resources.TryGetValue("MoveActions", out object? previous) && previous is int[] ids)
			{
				foreach (int id in ids) { AndroidX.Core.View.ViewCompat.RemoveAccessibilityAction(view, id); }
			}
			List<int> actions = [];
			if (index > 0) { actions.Add(AndroidX.Core.View.ViewCompat.AddAccessibilityAction(view, i18n.Translate("Move up"), new MoveAction(() => Move(entry, -1)))); }
			if (index >= 0 && index < _rows.Count - 1) { actions.Add(AndroidX.Core.View.ViewCompat.AddAccessibilityAction(view, i18n.Translate("Move down"), new MoveAction(() => Move(entry, 1)))); }
			row.Resources["MoveActions"] = actions.ToArray();
			view.ImportantForAccessibility = Android.Views.ImportantForAccessibility.Yes;
		}
#endif
	}

	private bool Move(KeyBarEntry entry, int offset)
	{
		int index = _rows.IndexOf(entry);
		if (index < 0 || index + offset < 0 || index + offset >= _rows.Count) { return false; }
		_rows.Move(index, index + offset);
		SaveOrder();
		return true;
	}

#if ANDROID
	// AndroidX requires a Java peer for each native accessibility action.
	private sealed class MoveAction(Func<bool> move) : Java.Lang.Object, AndroidX.Core.View.Accessibility.IAccessibilityViewCommand
	{
		public bool Perform(Android.Views.View? view, AndroidX.Core.View.Accessibility.AccessibilityViewCommandCommandArguments? arguments) => move();
	}
#endif

	private void Back_Clicked(object? sender, EventArgs e) => _ = App.GoToAsync("..");
}
