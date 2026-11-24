namespace Popups;

/// <summary>
/// Popup helper hardened against NullReferenceExceptions and UI-thread hazards.
/// • SafeExecuteAsync funnels all errors → DisplayAlert
/// • Page resolution + visual-tree mutation always run on the UI thread (Shell/Navigation are UI-affine)
/// • The result TaskCompletionSource is ALWAYS completed, so a caller can never hang — even when a
///   popup of the same type is already showing or there is no host layout
/// • No LINQ LastOrDefault (avoids CA1826) and no Task crosses WinRT ABI
/// </summary>
public static partial class PopupsHandle
{
    /*──────────────────────── 1 · SAFETY FUNNEL ────────────────────────*/

    private static async Task<TResult?> SafeExecuteAsync<TResult>(Func<Task<TResult?>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            await HandleErrorAsync(ex);
            return default;
        }
    }

    private static async Task HandleErrorAsync(Exception ex)
    {
        try
        {
            // GetCurrentPage + DisplayAlertAsync are UI-thread-affine; marshal before touching them.
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (GetCurrentPage() is Page p)
                    await p.DisplayAlertAsync(Languages.i18n.Translate("Error"), ex.Message, Languages.i18n.Translate("OK"));
            });
        }
        catch { /* never throw from the error path */ }
    }

    /*──────────────────────── 2 · PUBLIC POP-UPS ───────────────────────*/

    public static Task<string?> PopDisplayAsync(
        string Title,
        string Message,
        string[]? Buttons = null,
        bool Destructive = false,
        Action<Action>? Handle = null,   // receives the call that closes this very box (the caller's own, not the topmost)
        Action? Shown = null,
        Func<ContentPage, Layout, bool>? CanInsert = null)
        => ShowAsync<DisplayMsg, string>(
            () => { var box = new DisplayMsg(Title, Message, Buttons, Destructive); Handle?.Invoke(box.Dismiss); return box; },
            (msgBox, finish) => msgBox.Button_Clicked += (_, answer) => finish(answer), Shown, CanInsert);

    // The choice sheet: the tapped option, null on Cancel or when the dimmed page is tapped. selected = the current option (drawn with a check).
    public static Task<string?> PopListPickerAsync(List<string> items, string title, string? selected, Func<ContentPage, Layout, bool>? CanInsert = null)
        => PopBottomSheetAsync<string?>(close => new ListPicker(items, title, selected, close), CanInsert: CanInsert);

    // A bottom sheet: the page dimmed, a card panel that slides up from the bottom (the same custom animation on both platforms) and rises above
    // the soft keyboard (the page root's bottom safe area carries the keyboard, App.ApplyKeyboardSafeArea). Body builds the sheet's content from
    // the close callback; the result is what the body closes with, Dismissed when the dimmed page is tapped, null when no sheet could show.
    public static Task<T?> PopBottomSheetAsync<T>(Func<Action<T?>, ContentView> Body, T? Dismissed = default, Func<ContentPage, Layout, bool>? CanInsert = null)
    {
        T? result = Dismissed;
        BottomSheet? sheet = null;
        return ShowAsync<BottomSheet, T>(
            () => sheet = new BottomSheet(Body(answer => sheet!.Close(() => result = answer))),
            (_, finish) => sheet!.Closed += (_, _) => finish(result), canInsert: CanInsert);
    }

    // The one host of every popup: one popup of its type at a time (a second request resolves default = Cancel, as does a page without a layout, so a
    // caller never hangs), spans the whole grid, and answers through RemoveQueued so the result goes out after the popup left the layout.
    private static Task<TResult?> ShowAsync<TPopup, TResult>(Func<TPopup> create, Action<TPopup, Action<TResult?>> wire,
        Action? shown = null, Func<ContentPage, Layout, bool>? canInsert = null)
        where TPopup : View
        => SafeExecuteAsync<TResult>(async () =>
        {
            await HideKeyboardAsync();
            var tcs = new TaskCompletionSource<TResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (GetCurrentPage()?.Content is Layout layout
                    && layout.Children.FirstOrDefault(c => c is TPopup) is null)
                {
                    TPopup popup = create();

                    if (layout is Grid grid)
                    {
                        Grid.SetRow(popup, 0);
                        Grid.SetColumn(popup, 0);
                        Grid.SetRowSpan(popup, Math.Max(1, grid.RowDefinitions.Count));
                        Grid.SetColumnSpan(popup, Math.Max(1, grid.ColumnDefinitions.Count));
                    }

                    wire(popup, answer => RemoveQueued(layout, popup, () => tcs.TrySetResult(answer)));

                    // Keyboard hiding and creation callbacks can navigate, stop the host, or open another popup.
                    // The per-call guard is the last work before insertion; other callers retain the same-type-only policy.
                    if (canInsert is not null && (GetCurrentPage() is not { Content: Layout actual } page || !canInsert(page, actual)))
                    {
                        tcs.TrySetResult(default);
                        return;
                    }
                    layout.Children.Add(popup);
                    PaintHostPage(layout);
                    shown?.Invoke();
                }
                else
                {
                    tcs.TrySetResult(default);
                }
            });

            return await tcs.Task;
        });

    // The buttons of a message popup: Buttons[0] is the affirmative (left), Buttons[1] the second; an empty caption keeps its button hidden.
    // A box that passes a third button shows up to three; without one the second shows only when exactly two are given.
    internal static void SetButtons(Button left, Button right, string[]? buttons, Button? third = null)
    {
        if (buttons is { Length: >= 1 } && !string.IsNullOrEmpty(buttons[0])) left.Text = buttons[0];
        if (buttons is { Length: >= 2 } && (buttons.Length == 2 || third is not null) && !string.IsNullOrEmpty(buttons[1])) right.Text = buttons[1];
        if (third is not null && buttons is { Length: 3 } && !string.IsNullOrEmpty(buttons[2])) third.Text = buttons[2];
    }

    // Destructive confirmations: the affirmative button is the red primary.
    internal static void MakeDestructive(Button affirmative)
    {
        if (Application.Current?.Resources.TryGetValue("Btn.Destructive", out object? style) == true && style is Style destructive)
            affirmative.Style = destructive;
    }

    // A page that is leaving (its pane ended, the line dropped, its own close) closes every popup it still shows, each the way its
    // own Cancel does, and returns once they are off its layout: each caller's task then ends with its dismissed answer instead of
    // waiting on a page that is gone. Bounded, so a popup that never leaves cannot hold the page.
    public static Task CloseAllAsync(ContentPage page)
        => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (page.Content is not Layout layout) { return; }
            List<View> open = [.. layout.Children.OfType<View>().Where(c => c is BottomSheet or DisplayMsg)];
            if (open.Count == 0) { return; }
            TaskCompletionSource gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Removed(object? sender, ElementEventArgs e)
            {
                if (!layout.Children.Any(c => c is BottomSheet or DisplayMsg)) { gone.TrySetResult(); }
            }
            layout.ChildRemoved += Removed;
            try
            {
                foreach (View popup in open)
                {
                    if (popup is BottomSheet sheet) { sheet.Close(); }
                    else if (popup is DisplayMsg msg) { msg.Dismiss(); }
                }
                await gone.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException) { }
            finally { layout.ChildRemoved -= Removed; }
        });

    /*──────────────────────── 3 · POPUP REMOVAL ────────────────────────*/

    // A popup's tap handlers run inside Android's touch dispatch. Removing the popup there recycles the
    // touch targets the dispatch loop is still walking: with a second finger resting on the popup, Android
    // up to 14 (no null guard in ViewGroup.dispatchTouchEvent) dies with a NullPointerException in
    // ViewGroup.resetCancelNextUpFlag. Queued, the removal runs once the touch event has been delivered.
    // The answer goes out only after the removal: a popup the caller opens next would otherwise find this one still on the layout and resolve
    // at once (iOS runs the awaiting code before the queued removal).
    private static void RemoveQueued(Layout layout, IView popup, Action removed)
    {
        void Remove()
        {
            if (layout.Children.Contains(popup))
                layout.Children.Remove(popup);
            PaintHostPage(layout);
            removed();
        }

        if (!layout.Dispatcher.Dispatch(Remove))
            Remove();
    }

    // SafeAreaEdges=All leaves status-bar and home-indicator bands outside the scrim on iOS and Android 15+.
    // Paint the page with the scrim composite to dim those bands, but keep the content root's original color
    // so the scrim does not dim it twice. Restore both style values when the last popup closes.
    private static void PaintHostPage(Layout layout)
    {
        Element? e = layout.Parent;
        while (e is not null and not Page) e = e.Parent;
        if (e is not Page page) return;

        page.ClearValue(VisualElement.BackgroundColorProperty);   // always start from the style color so nested popups never dim twice
        layout.ClearValue(VisualElement.BackgroundColorProperty);
        bool open = layout.Children.Any(c => c is DisplayMsg or BottomSheet);

        if (open && page.BackgroundColor is { } bg
            && Application.Current is { } app
            && app.Resources.TryGetValue("Overlay", out object? o) && o is Color scrim)
        {
            float a = scrim.Alpha;   // same color and alpha as the Scrim style
            layout.BackgroundColor = bg;
            page.BackgroundColor = new Color(
                scrim.Red * a + bg.Red * (1 - a),
                scrim.Green * a + bg.Green * (1 - a),
                scrim.Blue * a + bg.Blue * (1 - a));
        }
#if ANDROID
        // The page applies its safe area as padding on its platform view, and an Android view group clips its children to its padding: while a
        // bottom sheet is open the page lets it draw into the bottom band (BottomSheet hangs its panel there), and clips again once it leaves.
        bool sheet = layout.Children.Any(c => c is BottomSheet);
        if (page.Handler?.PlatformView is Android.Views.ViewGroup pageView)
            pageView.SetClipToPadding(!sheet);

        // Up to Android 14 the system bars are opaque bands painted by App.SetSystemBarIcons, not the page showing through: give them the composite
        // while a popup is open (the navigation band takes the sheet's fill while a bottom sheet is open, as BottomSheet paints that band on
        // Android 15+), and let SetSystemBarIcons restore them afterwards.
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            if (open && page.BackgroundColor is { } band && Platform.CurrentActivity?.Window is { } window)
                HerdrRemote.App.PaintSystemBars(window, band, sheet
                    && Application.Current?.Resources.TryGetValue(Application.Current.RequestedTheme == AppTheme.Dark ? "CardFillDark" : "CardFill", out object? card) == true
                    ? card as Color : null);
            else if (!open)
                HerdrRemote.App.SetSystemBarIcons();
        }
#endif
    }

    /*──────────────────────── 3b · SOFT KEYBOARD ───────────────────────*/

    // The platform hook: hides the soft keyboard at the window level (Platforms/<os>/PopupsHandle.<os>.cs).
    private static partial void HideWindowKeyboard();

    // A popup over a focused field (Entry or Editor) leaves the soft keyboard open behind it. Closed here once, for every popup,
    // so pages need no per-call code.
    internal static Task HideKeyboardAsync()
        => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                if (GetCurrentPage() is not ContentPage page) { return; }

                // The tracked field first (HerdrRemote.App.KeepFocusedFieldVisible); a page without it is walked; a tracking page with no focused tracked field has nothing to unfocus.
                InputView? entry = HerdrRemote.App.FocusedEntry is { IsFocused: true } tracked ? tracked
                    : HerdrRemote.App.TrackedPages.TryGetValue(page, out _) ? null
                    : page.GetVisualTreeDescendants().OfType<InputView>().FirstOrDefault(e => e is Entry or Editor && e.IsFocused);
                if (entry is not null)
                {
                    await entry.HideSoftInputAsync(CancellationToken.None);
                    entry.Unfocus();
                }
            }
            // The window itself, not a focused field: a hardware Enter has already cleared the field's focus when the page changes.
            finally { HideWindowKeyboard(); }
        });

    /*──────────────────────── 4 · PAGE RESOLUTION ──────────────────────*/

    internal static ContentPage? GetCurrentPage()
    {
        if (HerdrRemote.App.CurrentShell?.CurrentPage is ContentPage cp)
            return cp;

        // Modal stack – indexer avoids LINQ/CA1826
        var modalStack = HerdrRemote.App.CurrentShell?.Navigation?.ModalStack;
        if (modalStack is { Count: > 0 } && modalStack[^1] is ContentPage modal)
            return modal;

        var navStack = HerdrRemote.App.CurrentShell?.Navigation?.NavigationStack;
        if (navStack is { Count: > 0 } && navStack[^1] is ContentPage nav)
            return nav;

        return null;
    }
}
