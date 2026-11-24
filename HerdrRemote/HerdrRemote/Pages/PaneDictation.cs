using HerdrRemote.Remote;
using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Pane dictation controls: mic, draft overlay, and send actions. App.Dictation owns state;
// subscribe only while _onScreen, handle user actions, and use the same paste and Enter encoding as other input.
public partial class Pane
{
	private bool _dictationShown;

	private string DictationKey => $"{ReadingMachine}/{_paneId}";

	private void InitDictation()
	{
		Bar.MicTapped += Bar_MicTapped;
		Draft.SendTapped += Draft_SendTapped;
		Draft.DiscardTapped += Draft_DiscardTapped;
		Draft.LanguageTapped += App.Dictation.SwitchLanguage;
		Draft.RetryTapped += () => _ = App.Dictation.RetryAsync(Draft.Caret);
		Draft.LiveTapped += async () =>
		{
			Draft.FocusWhenEditing();
			await App.Dictation.StopAsync();
		};
	}

	// A lease started: the draft of this pane shows again, and the session's changes are followed.
	private void DictationRestore()
	{
		if (!_dictationShown)
		{
			_dictationShown = true;
			App.Dictation.Changed += DictationChanged;
			App.Dictation.Level += Draft.ShowLevel;
			App.Dictation.Alert += DictationAlert;
		}
		Draft.ApplyLanguage();
		App.Dictation.Attach(DictationKey);
		DictationChanged();
	}

	// The lease ended or the screen went: listening stops, the draft stays in memory for this pane.
	private void DictationLeave()
	{
		if (_dictationShown)
		{
			_dictationShown = false;
			App.Dictation.Changed -= DictationChanged;
			App.Dictation.Level -= Draft.ShowLevel;
			App.Dictation.Alert -= DictationAlert;
		}
		App.Dictation.Leave();
		Draft.IsVisible = false;
		Bar.ShowDictation(false, false);
	}

	private void DictationChanged()
	{
		if (!_onScreen || !_leaseActive) { return; }
		Draft.Render(App.Dictation);
		Bar.ShowDictation(App.Dictation.IsOpen, App.Dictation.State == DictationState.Listening);
	}

	private void Bar_MicTapped()
	{
		if (!_leaseActive || !_onScreen) { return; }
		App.Dictation.Attach(DictationKey);
		_ = App.Dictation.MicAsync(Draft.IsFieldFocused ? Draft.Caret : Draft.Text.Length);
	}

	private void DictationAlert(DictationAlert alert)
	{
		LeaseOrigin origin = Origin;
		_ = ShowDictationAlertAsync(origin, alert);
	}

	private async Task ShowDictationAlertAsync(LeaseOrigin origin, DictationAlert alert)
	{
		string settings = i18n.Translate("Open settings");
		string? answer = await PopupsHandle.PopDisplayAsync(i18n.Translate("Dictation"), alert.Message,
			alert.OpenSettings ? [settings, i18n.Translate("Cancel")] : [i18n.Translate("OK")], CanInsert: InsertionGuard(origin));
		if (alert.OpenSettings && answer == settings) { AppInfo.Current.ShowSettingsUI(); }
	}

	// Paste the whole draft, bracketed when requested by the program, then send Enter if requested.
	// Stop listening first and include the final words. Clear the draft only when the terminal accepts the bytes.
	private bool _sending;

	private async void Draft_SendTapped(bool enter)
	{
		if (_sending) { return; }
		LeaseOrigin origin = Origin;
		_sending = true;
		bool ready;
		try { ready = await App.Dictation.StopForSendAsync(); }
		finally { _sending = false; }
		if (!ready || !Current(origin)) { return; }
		string text = App.Dictation.Text;
		if (text.Trim().Length == 0) { return; }
		// Send without Enter leaves the line open for more: a space after it, so the next dictation or typing does not stick to it.
		if (!enter && !char.IsWhiteSpace(text[^1])) { text += " "; }
		if (!Send(TerminalInput.Text(text, true, Screen.Modes)))
		{
			App.Dictation.Warn(i18n.Translate("No connection to this terminal. The draft is still here."));
			return;
		}
		// The keyboard stays up for the terminal when the editor held it, so the terminal is not resized twice.
		if (Draft.IsFieldFocused) { Input.Open(); }
		if (enter) { Send(TerminalInput.Key("enter", Screen.Modes)); }
		App.Dictation.Clear();
	}

	private async void Draft_DiscardTapped()
	{
		if (App.Dictation.Text.Length == 0) { App.Dictation.Clear(); return; }
		LeaseOrigin origin = Origin;
		string discard = i18n.Translate("Discard");
		string? answer = await PopupsHandle.PopDisplayAsync(i18n.Translate("Dictation"), i18n.Translate("Discard this draft?"),
			[discard, i18n.Translate("Cancel")], Destructive: true, CanInsert: InsertionGuard(origin));
		if (answer == discard && Current(origin)) { App.Dictation.Clear(); }
	}
}
