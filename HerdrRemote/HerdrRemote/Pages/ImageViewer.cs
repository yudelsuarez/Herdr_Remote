using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Languages;
using Microsoft.Maui.Graphics.Platform;
using Microsoft.Maui.Controls.Shapes;
using Popups;
using Path = System.IO.Path;

namespace HerdrRemote.Pages;

// Full-size terminal image overlay, opened from a thumbnail; only one full image is decoded at a time.
// The header shows "Image i of n" and Close; the bottom bar offers Markup, Save to photos, Share, and Send to agent.
// Share uses a temporary file deleted on close. Send uploads to the Mac and pastes its path.
// Pinch around the midpoint up to 8×; pan with one finger while zoomed; double-tap toggles 1× / 2.5× at the
// tap point. Pinching below 1× springs back; resizing resets to 1×. Behavior matches on iOS and Android.
// Use UIKit's pinch recognizer on iOS: MAUI 10.0.120 GesturePlatformManager retains scale after cancellation,
// so the next pinch can jump to 0.5× and spring back to 1×.
// During gestures and settling, use Scale and Translation anchored at the top-left relative to committed
// canvas bounds, avoiding layout on each step. Commit zoom to bounds at gesture/animation end and reset the
// transform to identity for sharp rendering and bounds that match the clamps.
// Keep this as an overlay, not a page: hiding the terminal page would reattach its stream and lose images.
public sealed class ImageViewer : ContentView
{
	private const double MaxZoom = 8;
	// How far a pinch may shrink the image before it springs back to 1×.
	private const double MinPinch = 0.5;
	private const double DoubleTapZoom = 2.5;
	private const string ZoomAnimation = "zoom";

	private byte[] _png;
	private readonly FitCanvas _canvas;
	private readonly Border _frame;
	private readonly Image _image;
	private readonly MarkupCanvas _markup;
	private readonly Grid _viewHeader, _editHeader;
	private readonly HorizontalStackLayout _actions, _colors;
	private readonly ScrollView _palette;
	private readonly Button _cancel, _undo, _redo, _done;
	private readonly Grid _progress;
	private readonly Spinner _spinner;
	private readonly PanGestureRecognizer _panGesture;
	private readonly TapGestureRecognizer _doubleTapGesture;
#if !IOS
	private readonly PinchGestureRecognizer _pinchGesture;
#endif
	private bool _editing, _exporting;
	// The image's pixel size (from its PNG header, as KittyGraphics reads it), for the frame's aspect.
	private readonly int _pixelWidth;
	private readonly int _pixelHeight;
	// The canvas inset the frame keeps at 1×.
	private readonly Thickness _gutter;
	// The frame at 1× (the image fitted inside the canvas less the gutter), the zoom, and the frame's top-left on the canvas.
	private double _fitWidth;
	private double _fitHeight;
	// The canvas size the frame was last fitted to: only a different size fits it again.
	private Size _fitted;
	private double _scale = 1;
	private double _x;
	private double _y;
	// The zoom and top-left last committed to the frame's bounds; the transform shows the rest.
	private double _boundsScale = 1;
	private double _boundsX;
	private double _boundsY;
	private bool _pinching;
	private bool _closed;
	private enum PanKind { None, Candidate, Dismissing, ZoomPan }
	private PanKind _panKind;
	private int? _panId;
	private double _panX, _panY;
	private bool _panRunning;
#if IOS
	// Whether the last iOS pinch update had two fingers (a finger lifted and put back starts a new baseline).
	private bool _twoFingers;
	// UIKit's pinch on the canvas: its target is this viewer, so Close takes it off its view and disposes it (native-managed cycle).
	private UIKit.UIPinchGestureRecognizer? _nativePinch;
#endif
	// A pan's start, and whether it must be taken again (a pan that outlived a pinch, whose totals count from before the pinch).
	private double _panStartX;
	private double _panStartY;
	private bool _panRebase;
	// The share sheet's temporary file, if any.
	private string? _shared;

	// The viewer is done (Close, or an image sent): the Pane screen takes it off.
	public event Action? Closed;

	// Title: the header's first word ("Image"), followed by the image's place "index of count"; subtitle: the terminal's name, space and program.
	internal ImageViewer(byte[] png, Func<byte[], Task> send, string title, int index, int count, string subtitle)
	{
		_png = png;
		_pixelWidth = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16));
		_pixelHeight = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
		_gutter = App.Res<Thickness>("Gutter");
		this.SetAppThemeColor(BackgroundColorProperty, App.Res<Color>("Bg"), App.Res<Color>("BgDark"));
		_image = new Image { Source = ImageSource.FromStream(() => new MemoryStream(png)), Aspect = Aspect.AspectFit };
		_markup = new MarkupCanvas(_pixelWidth, _pixelHeight, UpdateMarkup) { IsVisible = false };
		// Anchor the image frame's transforms at its top-left corner.
		_frame = new Border { Style = App.Res<Style>("Viewer.Frame"), Content = new Grid { Children = { _image, _markup } }, AnchorX = 0, AnchorY = 0 };
		// Fit and place the frame in the canvas's arrange pass. Unbounded AbsoluteLayout children otherwise appear
		// at their native image size in the top-left corner. Do not change bounds from SizeChanged during arrange:
		// the platform may not perform another layout pass.
		_canvas = new FitCanvas(Place) { IsClippedToBounds = true, Children = { _frame } };

#if IOS
		_canvas.HandlerChanged += (_, _) =>
		{
			if (_closed || _nativePinch is not null || _canvas.Handler?.PlatformView is not UIKit.UIView view) { return; }
			_nativePinch = new UIKit.UIPinchGestureRecognizer(NativePinch) { Enabled = !_editing };
			view.AddGestureRecognizer(_nativePinch);
		};
#else
		PinchGestureRecognizer pinch = _pinchGesture = new();
		pinch.PinchUpdated += (_, e) => Pinch(e);
		_canvas.GestureRecognizers.Add(pinch);
#endif
		PanGestureRecognizer pan = _panGesture = new();
		pan.PanUpdated += (_, e) => Pan(e);
		TapGestureRecognizer doubleTap = _doubleTapGesture = new() { NumberOfTapsRequired = 2 };
		doubleTap.Tapped += (_, e) => DoubleTap(e.GetPosition(_canvas));
		_canvas.GestureRecognizers.Add(pan);
		_canvas.GestureRecognizers.Add(doubleTap);

		FormattedString titleSpans = new() { Spans = { new Span { Text = title }, new Span { Style = App.Res<Style>("Fld.LabelSuffixSpan"), Text = $" {string.Format(CultureInfo.CurrentCulture, i18n.Translate("{0} of {1}"), index, count)}" } } };
		Header header = new() { SubtitleText = subtitle };

		Button close = new() { Style = App.Res<Style>("Hdr.Back"), Text = "\xE91D" };
		SemanticProperties.SetDescription(close, i18n.Translate("Close"));
		close.Clicked += (_, _) => Close();
		Grid lead = new() { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
		lead.Add(close);
		lead.Add(new Label { Style = App.Res<Style>("Hdr.Title"), FormattedText = titleSpans }, 1);
		header.Lead = lead;
		_viewHeader = new Grid { Children = { header } };
		Button pen = new() { Style = App.Res<Style>("TrayKey.IconAcc"), Text = "\xE95D" };
		SemanticProperties.SetDescription(pen, i18n.Translate("Markup"));
		pen.Clicked += (_, _) =>
		{
			if (_closed) { return; }
			this.AbortAnimation(ZoomAnimation);
			ClearPan();
			_pinching = false;
			_scale = 1;
			(_x, _y) = (ClampX(0, 1), ClampY(0, 1));
			Apply();
			_editing = true;
			UpdateMarkup();
		};

		Button save = new() { Style = App.Res<Style>("TrayKey.Icon"), Text = "\xE955" };
		SemanticProperties.SetDescription(save, i18n.Translate("Save to photos"));
		save.Clicked += async (_, _) => await ActAsync(async () =>
		{
			await Services.PhotoLibrary.SaveAsync(_png, $"remoteshell-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.png");
			await BusyRunner.Show("Image", i18n.Translate("Saved to the phone's photos."));
		});
		Button share = new() { Style = App.Res<Style>("TrayKey.Icon"), Text = "\xE90C" };
		SemanticProperties.SetDescription(share, i18n.Translate("Share"));
		share.Clicked += async (_, _) => await ActAsync(async () =>
		{
			_shared ??= Path.Combine(FileSystem.CacheDirectory, $"image-{Guid.NewGuid():N}.png");
			await File.WriteAllBytesAsync(_shared, _png);
			await Share.Default.RequestAsync(new ShareFileRequest { Title = i18n.Translate("Share"), File = new ShareFile(_shared, "image/png") });
		});
		Button agent = new() { Style = App.Res<Style>("TrayKey.Icon"), Text = "\xE94E" };
		SemanticProperties.SetDescription(agent, i18n.Translate("Send to agent"));
		agent.Clicked += async (_, _) => await ActAsync(async () =>
		{
			await send(_png);
			Close();
		});
		_actions = new HorizontalStackLayout { Style = App.Res<Style>("Row.Gap8"), HorizontalOptions = LayoutOptions.Center,
			Margin = new Thickness(0, 10, 0, 16), Children = { pen, save, share, agent } };

		_cancel = new Button { Style = App.Res<Style>("Hdr.Back"), Text = "\xE91D" };
		SemanticProperties.SetDescription(_cancel, i18n.Translate("Cancel"));
		_cancel.Clicked += async (_, _) => await ActAsync(async () =>
		{
			if (_exporting || !_editing || _closed) { return; }
			if (_markup.HasStrokes)
			{
				string discard = i18n.Translate("Discard");
				string? answer = await PopupsHandle.PopDisplayAsync(i18n.Translate("Markup"), i18n.Translate("Discard changes?"),
					[discard, i18n.Translate("Cancel")], Destructive: true, CanInsert: (_, _) => !_closed && _editing && !_exporting);
				if (_closed || answer != discard) { return; }
			}
			_editing = false;
			_markup.Clear();
			UpdateMarkup();
		});
		_undo = new Button { Style = App.Res<Style>("TrayKey.Icon"), Text = "\xE970" };
		_redo = new Button { Style = App.Res<Style>("TrayKey.Icon"), Text = "\xE971" };
		SemanticProperties.SetDescription(_undo, i18n.Translate("Undo"));
		SemanticProperties.SetDescription(_redo, i18n.Translate("Redo"));
		_undo.Clicked += (_, _) => _markup.Undo();
		_redo.Clicked += (_, _) => _markup.Redo();
		_done = new Button { Style = App.Res<Style>("TrayKey.Primary"), FontFamily = "icons", FontSize = 22, Text = "\xE91C", WidthRequest = 44, Padding = 0 };
		SemanticProperties.SetDescription(_done, i18n.Translate("Done"));
		_progress = new Grid { Style = App.Res<Style>("Agent.Spin"), IsVisible = false, InputTransparent = true };
		_progress.Add(new Ellipse { Style = App.Res<Style>("Agent.SpinTrack") });
		_progress.Add(new Microsoft.Maui.Controls.Shapes.Path { Style = App.Res<Style>("Agent.SpinArc"),
			Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M8,2 A6,6 0 0 1 14,8")! });
		_spinner = new Spinner(_progress);
		_done.Clicked += async (_, _) =>
		{
			if (_closed || _exporting || !_editing || !_markup.HasStrokes || _markup.IsDrawing) { return; }
			_exporting = true;
			UpdateMarkup();
			try
			{
				// Let the disabled controls and spinner reach the platform before the main-thread bitmap work.
				await Task.Yield();
				if (_closed) { return; }
				if (_pixelWidth <= 0 || _pixelHeight <= 0 || (long)_pixelWidth * _pixelHeight > 8_388_608) { throw new InvalidOperationException("Image exceeds the markup bitmap limit."); }
				using MemoryStream source = new(_png, false);
				using Microsoft.Maui.Graphics.IImage image = PlatformImage.FromStream(source);
				using BitmapExportContext bitmap = new PlatformBitmapExportService().CreateContext(_pixelWidth, _pixelHeight);
				bitmap.Canvas.DrawImage(image, 0, 0, _pixelWidth, _pixelHeight);
				_markup.Paint(bitmap.Canvas);
				using Microsoft.Maui.Graphics.IImage result = bitmap.Image;
				byte[] png = result.AsBytes(ImageFormat.Png);
				if (_closed) { return; }
				_png = png;
				_image.Source = ImageSource.FromStream(() => new MemoryStream(png, false));
				_editing = false;
				_markup.Clear();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Viewer] markup failed: {ex.GetType().Name}: {ex.Message}");
				_exporting = false;
				UpdateMarkup();
				if (!_closed) { await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again")); }
			}
			finally
			{
				_exporting = false;
				_spinner.Stop();
				UpdateMarkup();
			}
		};
		_editHeader = new Grid { Style = App.Res<Style>("Hdr"), IsVisible = false,
			ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
		_editHeader.Add(_cancel);
		_editHeader.Add(new HorizontalStackLayout { Style = App.Res<Style>("Row.Gap6"), VerticalOptions = LayoutOptions.Center, Children = { _undo, _redo } }, 2);
		_editHeader.Add(new Grid { VerticalOptions = LayoutOptions.Center, Children = { _done, _progress } }, 4);
#if IOS
		if (OperatingSystem.IsIOSVersionAtLeast(26)) { BindWindowControlMargins(_editHeader); }
#endif
		_colors = new HorizontalStackLayout { Style = App.Res<Style>("Row.Gap8"), HorizontalOptions = LayoutOptions.Center,
			Margin = new Thickness(0, 10, 0, 16) };
		foreach (string hex in new[] { "#E5484D", "#F5C451", "#4CC38A", "#91B6EE", "#EEEEEF" })
		{
			Color ink = Color.FromArgb(hex);
			Border dot = new() { WidthRequest = 26, HeightRequest = 26, BackgroundColor = ink, StrokeThickness = 2,
				StrokeShape = new RoundRectangle { CornerRadius = 13 }, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
			Button color = new() { BackgroundColor = Colors.Transparent, WidthRequest = 44, HeightRequest = 44, Padding = 0, BorderWidth = 0 };
			SemanticProperties.SetDescription(color, $"{i18n.Translate("Markup")} {hex}");
			color.Clicked += (_, _) => { _markup.Ink = ink; UpdateMarkup(); };
			_colors.Add(new Grid { Children = { color, dot }, InputTransparent = false });
			dot.InputTransparent = true;
		}
		BoxView divider = new() { WidthRequest = 1, HeightRequest = 26, VerticalOptions = LayoutOptions.Center };
		divider.SetAppThemeColor(BoxView.ColorProperty, App.Res<Color>("Line"), App.Res<Color>("LineDark"));
		_colors.Add(divider);
		Button tool = new() { Style = App.Res<Style>("TrayKey.IconAcc"), Text = "\xE95D", InputTransparent = true };
		SemanticProperties.SetDescription(tool, i18n.Translate("Markup"));
		_colors.Add(tool);
		_palette = new ScrollView { Content = _colors, Orientation = ScrollOrientation.Horizontal,
			HorizontalOptions = LayoutOptions.Center, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, IsVisible = false };
		Content = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
			Children = { _viewHeader, _editHeader, _canvas, _actions, _palette } };
		Grid.SetRow(_canvas, 1);
		Grid.SetRow(_actions, 2);
		Grid.SetRow(_palette, 2);
		UpdateMarkup();
	}

#if IOS
	[System.Runtime.Versioning.SupportedOSPlatform("ios26.0")]
	private static void BindWindowControlMargins(Grid header)
	{
		Thickness padding = header.Padding;
		header.HandlerChanged += (_, _) =>
		{
			if (header.Handler?.PlatformView is not UIKit.UIView native) { return; }
			using UIKit.UIViewLayoutRegion region = UIKit.UIViewLayoutRegion.CreateMarginsLayoutRegion(
				UIKit.UIViewLayoutRegionAdaptivityAxis.Horizontal);
			UIKit.UILayoutGuide guide = native.GetLayoutGuide(region);
			// Follow UIKit's guide on live window and control changes, not just when the managed width changes.
			// One probe per native header. The probe reaches its container through Superview and the header through a weak
			// reference, so it holds nothing that keeps the closed viewer's views alive.
			foreach (UIKit.UIView old in native.Subviews.OfType<WindowControlMargins>().ToArray()) { old.RemoveFromSuperview(); }
			WeakReference<Grid> owner = new(header);
			WindowControlMargins probe = new((frame, container) =>
			{
				if (frame.Width <= 0 || !owner.TryGetTarget(out Grid? header)) { return; }
				double left = Math.Max(padding.Left, (double)frame.Left);
				double right = Math.Max(padding.Right, (double)(container.Bounds.Width - frame.Right));
				Thickness next = new(left, padding.Top, right, padding.Bottom);
				if (header.Padding != next) { header.Padding = next; }
			});
			native.AddSubview(probe);
			UIKit.NSLayoutConstraint.ActivateConstraints([
				probe.LeftAnchor.ConstraintEqualTo(guide.LeftAnchor),
				probe.RightAnchor.ConstraintEqualTo(guide.RightAnchor),
				probe.TopAnchor.ConstraintEqualTo(native.TopAnchor),
				probe.HeightAnchor.ConstraintEqualTo(1)
			]);
		};
	}

	private sealed class WindowControlMargins : UIKit.UIView
	{
		private readonly Action<CoreGraphics.CGRect, UIKit.UIView> _changed;

		public WindowControlMargins(Action<CoreGraphics.CGRect, UIKit.UIView> changed)
		{
			_changed = changed;
			TranslatesAutoresizingMaskIntoConstraints = false;
			UserInteractionEnabled = false;
			AccessibilityElementsHidden = true;
		}

		public override void LayoutSubviews()
		{
			base.LayoutSubviews();
			if (Superview is { } container) { _changed(Frame, container); }
		}
	}
#endif

	private void UpdateMarkup()
	{
		if (_closed) { return; }
		// A recognizer can cancel the child's touch even when its managed callback returns without handling it.
		if (_canvas.GestureRecognizers.Contains(_panGesture) == _editing)
		{
			if (_editing)
			{
				_canvas.GestureRecognizers.Remove(_panGesture);
				_canvas.GestureRecognizers.Remove(_doubleTapGesture);
#if !IOS
				_canvas.GestureRecognizers.Remove(_pinchGesture);
#endif
			}
			else
			{
#if !IOS
				_canvas.GestureRecognizers.Add(_pinchGesture);
#endif
				_canvas.GestureRecognizers.Add(_panGesture);
				_canvas.GestureRecognizers.Add(_doubleTapGesture);
			}
		}
#if IOS
		if (_nativePinch is not null) { _nativePinch.Enabled = !_editing; }
#endif
		_viewHeader.IsVisible = _actions.IsVisible = !_editing;
		_editHeader.IsVisible = _palette.IsVisible = _markup.IsVisible = _editing;
		_markup.IsEnabled = _cancel.IsEnabled = _colors.IsEnabled = !_exporting;
		_colors.Opacity = _exporting ? 0.35 : 1;
		_undo.IsEnabled = !_exporting && _markup.CanUndo;
		_redo.IsEnabled = !_exporting && _markup.CanRedo;
		_done.IsEnabled = !_exporting && _markup.HasStrokes && !_markup.IsDrawing;
		_undo.Opacity = _undo.IsEnabled ? 1 : 0.35;
		_redo.Opacity = _redo.IsEnabled ? 1 : 0.35;
		_done.Opacity = _exporting || _done.IsEnabled ? 1 : 0.35;
		_done.Text = _exporting ? "" : "\xE91C";
		_progress.IsVisible = _exporting;
		if (_exporting) { _spinner.Start(); } else { _spinner.Stop(); }
		foreach (Grid item in _colors.Children.OfType<Grid>())
		{
			Border dot = (Border)item.Children[1];
			dot.SetAppTheme<Brush>(Border.StrokeProperty, new SolidColorBrush(dot.BackgroundColor.Equals(_markup.Ink) ? App.Res<Color>("Ink") : Colors.Transparent),
				new SolidColorBrush(dot.BackgroundColor.Equals(_markup.Ink) ? App.Res<Color>("InkDark") : Colors.Transparent));
		}
	}

	// Leaving the viewer (its Close, a sent image, or the Pane screen leaving): the share sheet's file goes (the sheet has taken its
	// copy by then), and on iOS the pinch recognizer leaves its view.
	public void Close()
	{
		if (_closed) { return; }
		_closed = true;
		_spinner.Stop();
		_markup.Clear();
		ClearPan();
		this.AbortAnimation(ZoomAnimation);
#if IOS
		if (_nativePinch is not null)
		{
			_nativePinch.View?.RemoveGestureRecognizer(_nativePinch);
			_nativePinch.Dispose();
			_nativePinch = null;
		}
#endif
		if (_shared is not null && File.Exists(_shared)) { File.Delete(_shared); }
		Closed?.Invoke();
	}

	// Arrange the frame at its committed bounds. A new canvas size (first layout, rotation, or keyboard change)
	// refits and centers the image at 1×; before the first fit, the frame has no size.
	private Rect Place(Size room)
	{
		Reset(room);
		return _fitWidth <= 0 ? Rect.Zero : new Rect(_boundsX, _boundsY, _fitWidth * _boundsScale, _fitHeight * _boundsScale);
	}

	// A new canvas size: the image fitted again, at 1× and centered. The same size again changes nothing.
	private void Reset(Size room)
	{
		double width = room.Width - _gutter.HorizontalThickness;
		double height = room.Height - _gutter.VerticalThickness;
		if (width <= 0 || height <= 0 || _pixelWidth <= 0 || _pixelHeight <= 0) { return; }
		// Less than half a point either way is the same size (layout rounding).
		if (Math.Abs(room.Width - _fitted.Width) < 0.5 && Math.Abs(room.Height - _fitted.Height) < 0.5) { return; }
		if (_panKind is PanKind.Candidate or PanKind.Dismissing) { CancelDismiss(); }
		ClearPan();
		this.AbortAnimation(ZoomAnimation);
		_fitted = room;
		double fit = Math.Min(width / _pixelWidth, height / _pixelHeight);
		(_fitWidth, _fitHeight) = (_pixelWidth * fit, _pixelHeight * fit);
		_scale = 1;
		(_x, _y) = (ClampX(0, 1), ClampY(0, 1));
		Commit();
	}

#if IOS
	// UIKit's pinch: its scale is read and set back to 1 on every step, so each step is the change since the last one and nothing
	// carries over to the next pinch. A step with one finger is skipped, and the first step after the second finger comes back only
	// takes a new baseline (UIKit changes the scale when the finger count changes).
	private void NativePinch(UIKit.UIPinchGestureRecognizer r)
	{
		if (_editing || _closed) { return; }
		switch (r.State)
		{
			case UIKit.UIGestureRecognizerState.Began:
				PinchStarted();
				_twoFingers = r.NumberOfTouches >= 2;
				r.Scale = 1;
				break;
			case UIKit.UIGestureRecognizerState.Changed:
				bool two = r.NumberOfTouches >= 2;
				if (two && _twoFingers)
				{
					CoreGraphics.CGPoint at = r.LocationInView(r.View);
					PinchBy(r.Scale, at.X, at.Y);
				}
				_twoFingers = two;
				r.Scale = 1;
				break;
			case UIKit.UIGestureRecognizerState.Ended:
			case UIKit.UIGestureRecognizerState.Cancelled:
			case UIKit.UIGestureRecognizerState.Failed:
				if (_pinching) { PinchEnded(); }
				break;
		}
	}
#else
	private void Pinch(PinchGestureUpdatedEventArgs e)
	{
		if (_editing || _closed) { return; }
		switch (e.Status)
		{
			case GestureStatus.Started:
				PinchStarted();
				break;
			case GestureStatus.Running:
				PinchBy(e.Scale, e.ScaleOrigin.X * _canvas.Width, e.ScaleOrigin.Y * _canvas.Height);
				break;
			default:
				PinchEnded();
				break;
		}
	}
#endif

	// Two fingers: the point between them stays on the same spot of the image while the zoom follows them; let go, the image settles
	// (back to 1× centered when shrunk below it, else inside the canvas).
	private void PinchStarted()
	{
		if (_fitWidth <= 0) { return; }
		CancelDismiss();
		StopAnimation();
		_pinching = true;
	}

	// One pinch step: the zoom times scale, around the canvas point (x, y).
	private void PinchBy(double scale, double x, double y)
	{
		if (!_pinching) { return; }
		ZoomAround(_scale * scale, x, y);
		Show();
	}

	private void PinchEnded()
	{
		if (!_pinching) { return; }
		_pinching = false;
		_panRebase = true;
		Settle(Math.Max(_scale, 1));
	}

	// At fitted scale a downward drag dismisses; zoomed pans keep the existing clamps and transform-only updates.
	private void Pan(PanUpdatedEventArgs e)
	{
		if (_closed || _editing || _fitWidth <= 0) { return; }
		if (e.StatusType == GestureStatus.Started)
		{
			StopAnimation();
			(_panStartX, _panStartY, _panRebase) = (_x, _y, false);
			_panId = e.GestureId;
			_panX = _panY = 0;
			_panRunning = false;
			_panKind = _pinching ? PanKind.None : _scale > 1.001 ? PanKind.ZoomPan
				: Math.Abs(_scale - 1) <= 0.001 ? PanKind.Candidate : PanKind.None;
			return;
		}
		if (_panId != e.GestureId || _pinching) { return; }
		if (e.StatusType == GestureStatus.Running)
		{
			(_panX, _panY, _panRunning) = (e.TotalX, e.TotalY, true);
			if (_panKind == PanKind.Candidate && _panX * _panX + _panY * _panY > 100)
			{
				_panKind = _panY > Math.Abs(_panX) ? PanKind.Dismissing : PanKind.None;
			}
			if (_panKind == PanKind.Dismissing)
			{
				(_x, _y) = (_panStartX, _panStartY + Math.Max(0, _panY));
				Show();
			}
			else if (_panKind == PanKind.ZoomPan)
			{
				if (_panRebase) { (_panStartX, _panStartY, _panRebase) = (_x - e.TotalX, _y - e.TotalY, false); }
				(_x, _y) = (ClampX(_panStartX + e.TotalX, _scale), ClampY(_panStartY + e.TotalY, _scale));
				Show();
			}
		}
		else if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled && !_pinching)
		{
			PanKind kind = _panKind;
			bool dismiss = e.StatusType == GestureStatus.Completed && kind == PanKind.Dismissing && _panRunning
				&& _panY >= 80 && _panY > Math.Abs(_panX);
			ClearPan();
			if (dismiss) { Close(); }
			else if (kind is PanKind.Candidate or PanKind.Dismissing) { Settle(1); }
			else if (kind == PanKind.ZoomPan) { Apply(); }
		}
	}

	private void ClearPan()
	{
		_panId = null;
		_panKind = PanKind.None;
		_panX = _panY = 0;
		_panRunning = false;
	}

	private void CancelDismiss()
	{
		if (_panKind is not (PanKind.Candidate or PanKind.Dismissing)) { return; }
		(_x, _y) = (_panStartX, _panStartY);
		ClearPan();
	}

	// A zoomed image goes back to 1×; at 1×, it zooms in at the tapped point (the canvas center when the platform gives no point).
	private void DoubleTap(Point? at)
	{
		if (_editing || _closed || _fitWidth <= 0) { return; }
		CancelDismiss();
		StopAnimation();
		if (_scale > 1) { Settle(1); return; }
		Point point = at ?? new Point(_canvas.Width / 2, _canvas.Height / 2);
		double x = point.X - (point.X - _x) * DoubleTapZoom;
		double y = point.Y - (point.Y - _y) * DoubleTapZoom;
		AnimateTo(DoubleTapZoom, ClampX(x, DoubleTapZoom), ClampY(y, DoubleTapZoom));
	}

	// The zoom changed to scale (within MinPinch..MaxZoom), keeping the canvas point (x, y) on the same spot of the image.
	private void ZoomAround(double scale, double x, double y)
	{
		scale = Math.Clamp(scale, MinPinch, MaxZoom);
		_x = x - (x - _x) * scale / _scale;
		_y = y - (y - _y) * scale / _scale;
		_scale = scale;
	}

	// The image eased to scale, inside the canvas: 1× is always centered.
	private void Settle(double scale) =>
		AnimateTo(scale, ClampX(scale == 1 ? 0 : _x, scale), ClampY(scale == 1 ? 0 : _y, scale));

	private void AnimateTo(double scale, double x, double y)
	{
		(double fromScale, double fromX, double fromY) = (_scale, _x, _y);
		new Animation(t =>
		{
			_scale = fromScale + (scale - fromScale) * t;
			_x = fromX + (x - fromX) * t;
			_y = fromY + (y - fromY) * t;
			Show();
		}).Commit(this, ZoomAnimation, length: 200, easing: Easing.CubicOut, finished: (_, _) => { if (!_closed) { Apply(); } });
	}

	// A running settle stops where it is, committed (a gesture that starts during it starts from committed bounds).
	private void StopAnimation()
	{
		this.AbortAnimation(ZoomAnimation);
		Apply();
	}

	// A frame position on one axis for a zoom: centered while the frame fits in the canvas, else no empty space past its edges.
	private double ClampX(double x, double scale) => Clamp(x, _fitWidth * scale, _fitted.Width);

	private double ClampY(double y, double scale) => Clamp(y, _fitHeight * scale, _fitted.Height);

	private static double Clamp(double position, double size, double room) =>
		size <= room ? (room - size) / 2 : Math.Clamp(position, room - size, 0);

	// One step of a gesture or of the settle: the frame's transform, relative to its committed bounds (no layout pass).
	private void Show()
	{
		_frame.Scale = _scale / _boundsScale;
		_frame.TranslationX = _x - _boundsX;
		_frame.TranslationY = _y - _boundsY;
	}

	// The zoom committed to the frame's bounds (one layout pass of the canvas), the transform back to identity.
	private void Apply()
	{
		Commit();
		((IView)_canvas).InvalidateMeasure();
	}

	// The zoom taken as the frame's bounds, which the canvas's next arrange uses (Place); the transform back to identity.
	private void Commit()
	{
		(_boundsScale, _boundsX, _boundsY) = (_scale, _x, _y);
		_frame.Scale = 1;
		_frame.TranslationX = 0;
		_frame.TranslationY = 0;
	}

	// Measure and arrange the frame in one canvas pass using the rectangle returned by place.
	// Never write bounds from a size event.
	private sealed class FitCanvas(Func<Size, Rect> place) : Layout
	{
		protected override Microsoft.Maui.Layouts.ILayoutManager CreateLayoutManager() => new Manager(this, place);

		private sealed class Manager(FitCanvas canvas, Func<Size, Rect> place) : Microsoft.Maui.Layouts.ILayoutManager
		{
			public Size Measure(double widthConstraint, double heightConstraint)
			{
				double width = double.IsFinite(widthConstraint) ? widthConstraint : 0;
				double height = double.IsFinite(heightConstraint) ? heightConstraint : 0;
				// Measuring changes nothing: the fit and the frame's place are taken in the arrange pass.
				foreach (IView child in canvas) { child.Measure(width, height); }
				return new Size(width, height);
			}

			public Size ArrangeChildren(Rect bounds)
			{
				Rect at = place(bounds.Size);
				foreach (IView child in canvas) { child.Arrange(at); }
				return bounds.Size;
			}
		}
	}

	// One action of the bar; a failure shows the generic message (and is logged), never closes the viewer.
	private static async Task ActAsync(Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Viewer] action failed: {ex.GetType().Name}: {ex.Message}");
			await BusyRunner.Show("Error", i18n.Translate("Something went wrong, please try again"));
		}
	}
}
