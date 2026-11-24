using System.Diagnostics;
using HerdrRemote.Services;
using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Read selected pasted text, or all text, with the chosen provider. Only Read starts playback.
// Leaving detaches the presentation without stopping the captured session.
public partial class Reader : ContentPage
{
	private enum Phase { Idle, Generating, Playing, Paused }

	private Phase _phase = Phase.Idle;

	private readonly ReadingSession _reader = App.Reading;
	private Window? _visibleWindow;

	public Reader()
	{
		InitializeComponent();
		ReadingBar reading = Header.TakePaneReading();
		reading.VerticalOptions = LayoutOptions.Center;
		ActionSlot.Add(reading, 1, 0);
		Tabs.Active = "Reader";
		App.ApplyKeyboardSafeArea(this);
		App.KeepFocusedFieldVisible(Ed_Text);
		Ed_Text.HandlerChanging += (_, _) => ClearReaderMarks();
		Ed_Text.Loaded += (_, _) => RefreshReaderMarks();
		Ed_Text.TextChanged += (_, _) => RefreshReaderMarks();
		Ed_Text.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == Editor.TextColorProperty.PropertyName && _markedFrom >= 0)
			{
				Dispatcher.Dispatch(() => RefreshReaderMarks(repaint: true));
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Header.AttachReading();
		AttachReader();
		_visibleWindow = Window;
		if (_visibleWindow is { } window) { window.Stopped += Window_Stopped; window.Resumed += Window_Resumed; }
		SetLanguage();
		Reader_Changed();
	}

	protected override void OnDisappearing()
	{
		Header.DetachReading();
		base.OnDisappearing();
		DetachReader();
		if (_visibleWindow is { } window) { window.Stopped -= Window_Stopped; window.Resumed -= Window_Resumed; }
		_visibleWindow = null;
	}
	private void Window_Stopped(object? sender, EventArgs e) => DetachReader();
	private void Window_Resumed(object? sender, EventArgs e)
	{
		DetachReader();
		AttachReader();
		Reader_Changed();
	}
	private void AttachReader()
	{
		_reader.Changed += Reader_Changed;
		_reader.ReaderSourceInvalidated += ClearReaderMarks;
	}
	private void DetachReader()
	{
		_reader.Changed -= Reader_Changed;
		_reader.ReaderSourceInvalidated -= ClearReaderMarks;
	}

	private void Reader_Changed()
	{
		SetPhase(_reader.Phase switch { "waiting" => Phase.Generating, "playing" => Phase.Playing, "paused" => Phase.Paused, _ => Phase.Idle });
		RefreshReaderMarks();
		if (!_reader.IsPreview && _reader.TakeError() is { } error) { _ = BusyRunner.Show("Read", error); }
	}

	private void SetLanguage()
	{
		Header.TitleText = i18n.Translate("Reader");
		Ed_Text.Placeholder = i18n.Translate("Paste the text here. Tap where to start reading, or select a part.");
		ShowVoice();
		SetPhase(_phase);
		Tabs.ApplyLanguage();
	}

	// The voice pill follows saved settings; browsing the sheet does not change them.
	private void ShowVoice()
	{
		string summary = VoiceSheet.Summary();
		Lbl_Voice.Text = summary;
		SemanticProperties.SetDescription(Cell, $"{i18n.Translate("Voice")}, {summary}");
	}

	private void SetPhase(Phase phase)
	{
		_phase = phase;
		Btn_Primary.IsVisible = phase == Phase.Idle;
		Btn_Primary.Text = "\uE950";
		SemanticProperties.SetDescription(Btn_Primary, i18n.Translate("Read"));
		Pill.IsEnabled = true;
	}

	// The selection when there is one, widened to whole words so no half word is read; else, with the cursor inside the text, from the start of the cursor's word to the end; else the whole text.
	// MAUI copies the native selection into CursorPosition / SelectionLength on every change.
	private static (string Text, int Offset) TextToRead(string all, int start, int length)
	{
		int end = all.Length;
		if (length > 0 && start >= 0 && length <= all.Length - start)
		{
			end = start + length;
			while (start > 0 && !char.IsWhiteSpace(all[start - 1])) { start--; }
			while (end < all.Length && !char.IsWhiteSpace(all[end])) { end++; }
		}
		else if (start > 0 && start < all.Length)
		{
			while (start > 0 && !char.IsWhiteSpace(all[start - 1])) { start--; }
		}
		else
		{
			start = 0;
		}
		while (start < end && char.IsWhiteSpace(all[start])) { start++; }
		while (end > start && char.IsWhiteSpace(all[end - 1])) { end--; }
		return (all[start..end], start);
	}

	private async void Btn_Read_Clicked(object? sender, EventArgs e)
	{
		string all = Ed_Text.Text ?? "";
		(string text, int offset) = TextToRead(all, Ed_Text.CursorPosition, Ed_Text.SelectionLength);
		if (text.Length == 0) { await BusyRunner.Show("Read", i18n.Translate("Paste some text first.")); return; }
		object? editor = Ed_Text.Handler?.PlatformView;
		Btn_Primary.IsEnabled = false;
		try
		{
			List<(string Text, int Paragraph)> sentences = PaneText.Sentences([text]);
			var ranges = PaneText.SourceRanges(text, sentences);
			for (int i = 0; i < ranges.Length; i++)
			{
				ranges[i] = ranges[i] is { } range && TtsProviders.Filter(sentences[i].Text) is not null
					? (offset + range.Start, range.Length) : null;
			}
			try
			{
				if (!await PrepareReaderStartAsync(editor, all, Array.Find(ranges, range => range.HasValue))) { return; }
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Reader] prepare reader unexpected: {ex}");
				await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
				return;
			}
			object source = new();
			Task reading = _reader.ReadReaderAsync(sentences, source);
			_readerPlan = new(source, all, ranges);
			RefreshReaderMarks();
			await reading;
		}
		finally
		{
			if (_markedFrom < 0 && Ed_Text.IsReadOnly && editor is not null) { SetReaderReadOnly(editor, false); }
			Btn_Primary.IsEnabled = true;
		}
	}

	private void Btn_Primary_Clicked(object? sender, EventArgs e)
	{
		if (_phase == Phase.Idle) { Btn_Read_Clicked(sender, e); }
	}

	// Browsing voices keeps the reading; only an explicitly confirmed sample replaces it.
	private async void Voice_Tapped(object? sender, TappedEventArgs e)
	{
		try
		{
			await VoiceSheet.ShowAsync();
			ShowVoice();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Reader] voice sheet unexpected: {ex.Message}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
		}
	}
}
