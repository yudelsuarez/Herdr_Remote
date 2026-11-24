using System.Diagnostics;

namespace HerdrRemote.Pages;

// Physical touch-down owns the 450 ms clock; MAUI Pressed may arrive late inside an iOS scroll view.
internal static class HoldTap
{
	internal const int HoldMs = 450;

	internal sealed class HoldButton : Button
	{
		internal readonly Press Press;
		internal Action? Tapped;
		internal Action? Held;
#if IOS
		private MoveWatcher? _watcher;
		private UIKit.UIView? _watched;
#endif

		public HoldButton()
		{
			Press = new Press(this);
			HandlerChanging += (_, _) => Detach();
			HandlerChanged += (_, _) => Attach();
			Unloaded += (_, _) => Detach();
			Loaded += (_, _) => Attach();
			Clicked += (_, _) => { if (Press.TakeTap()) { Tapped?.Invoke(); } };
		}

		protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
		{
			base.MeasureOverride(widthConstraint, heightConstraint);
			return Size.Zero;
		}

		internal void FireHold()
		{
			if (HapticFeedback.Default.IsSupported) { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); }
			Held?.Invoke();
		}

		private void Attach()
		{
#if IOS
			if (_watcher is not null || Handler?.PlatformView is not UIKit.UIView view) { return; }
			_watched = view;
			_watcher = new MoveWatcher(Press);
			view.AddGestureRecognizer(_watcher);
#elif ANDROID
			if (Handler?.PlatformView is DragWatchButton view) { view.Press = Press; }
#endif
		}

		private void Detach()
		{
			Press.Cancel();
#if IOS
			if (_watcher is not null)
			{
				_watched?.RemoveGestureRecognizer(_watcher);
				_watcher.Dispose();
				_watcher = null;
				_watched = null;
			}
#elif ANDROID
			if (Handler?.PlatformView is DragWatchButton view) { view.Press = null; }
#endif
		}
	}

	// Native watchers and the delayed callback hold only this state and a weak button reference, never row actions.
	internal sealed class Press(HoldButton button)
	{
		private readonly WeakReference<HoldButton> _button = new(button);
		private long _started;
		private int _generation;
		private bool _physical;
		public bool Down;
		public bool Fired;
		public bool Dragged;

		public void Begin()
		{
			_physical = Down = true;
			Fired = Dragged = false;
			_started = Stopwatch.GetTimestamp();
			int mine = ++_generation;
			if (_button.TryGetTarget(out HoldButton? target))
			{
				target.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(HoldMs), () => Fire(mine));
			}
		}

		public void Move()
		{
			Dragged = true;
			Down = false;
			_generation++;
		}

		public void Cancel()
		{
			Move();
		}

		public void End()
		{
			if (Down && Stopwatch.GetElapsedTime(_started).TotalMilliseconds >= HoldMs) { Fire(_generation); }
			Down = false;
			_generation++;
		}

		private void Fire(int mine)
		{
			if (!Down || mine != _generation || Dragged || Fired || !_button.TryGetTarget(out HoldButton? target) || target.Handler?.PlatformView is null || target.Held is null) { return; }
			Fired = true;
			target.FireHold();
		}

		public bool TakeTap()
		{
			if (!_physical) { return true; } // Accessibility/programmatic Clicked has no physical sequence.
			_physical = false;
			return !Fired && !Dragged;
		}
	}

#if IOS
	// Never recognizes or delays the button: it observes physical begin/end/cancel before MAUI's delayed Pressed.
	private sealed class MoveWatcher : UIKit.UIGestureRecognizer
	{
		private const double Slop = 10;
		private readonly Press _press;
		private CoreGraphics.CGPoint _start;

		public MoveWatcher(Press press)
		{
			_press = press;
			CancelsTouchesInView = false;
			DelaysTouchesBegan = false;
			DelaysTouchesEnded = false;
			ShouldRecognizeSimultaneously = static (_, _) => true;
		}

		public override void TouchesBegan(Foundation.NSSet touches, UIKit.UIEvent evt)
		{
			base.TouchesBegan(touches, evt);
			if (evt.AllTouches?.Count != 1 || touches.AnyObject is not UIKit.UITouch touch) { _press.Cancel(); State = UIKit.UIGestureRecognizerState.Failed; return; }
			_start = touch.LocationInView(null);
			_press.Begin();
		}

		public override void TouchesMoved(Foundation.NSSet touches, UIKit.UIEvent evt)
		{
			base.TouchesMoved(touches, evt);
			if (State != UIKit.UIGestureRecognizerState.Possible || touches.AnyObject is not UIKit.UITouch touch) { return; }
			CoreGraphics.CGPoint now = touch.LocationInView(null);
			if (Math.Abs(now.X - _start.X) <= Slop && Math.Abs(now.Y - _start.Y) <= Slop) { return; }
			_press.Move();
			State = UIKit.UIGestureRecognizerState.Failed;
		}

		public override void TouchesEnded(Foundation.NSSet touches, UIKit.UIEvent evt)
		{
			_press.End();
			base.TouchesEnded(touches, evt);
			State = UIKit.UIGestureRecognizerState.Failed;
		}

		public override void TouchesCancelled(Foundation.NSSet touches, UIKit.UIEvent evt)
		{
			_press.Cancel();
			base.TouchesCancelled(touches, evt);
			State = UIKit.UIGestureRecognizerState.Failed;
		}
	}
#endif

	// Cancel before detaching a removed identity, while its handler is still present.
	internal static void Cancel(Grid row)
	{
		foreach (HoldButton button in row.Children.OfType<HoldButton>()) { button.Press.Cancel(); }
	}

	public static Button Add(Grid row, string spoken, Action tapped, Action held)
	{
		HoldButton button = new() { Style = App.Res<Style>("Hold"), Tapped = tapped, Held = held };
		foreach (IView child in row.Children)
		{
			if (child is BindableObject content) { AutomationProperties.SetExcludedWithChildren(content, true); }
		}
		SemanticProperties.SetDescription(button, spoken);
		button.Margin = new Thickness(-row.Padding.Left, -row.Padding.Top, -row.Padding.Right, -row.Padding.Bottom);
		row.Add(button);
		Grid.SetColumnSpan(button, Math.Max(1, row.ColumnDefinitions.Count));
		Grid.SetRowSpan(button, Math.Max(1, row.RowDefinitions.Count));
		return button;
	}
}
