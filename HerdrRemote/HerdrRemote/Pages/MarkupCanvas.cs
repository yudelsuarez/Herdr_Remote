namespace HerdrRemote.Pages;

// Points and pen widths stay in image pixels; only the display transform changes when the viewer re-fits.
internal sealed class MarkupCanvas : GraphicsView, IDrawable
{
	private readonly int _pixelWidth, _pixelHeight;
	private readonly Action _changed;
	private readonly List<(Color Color, float Width, List<PointF> Points)> _strokes = [];
	private readonly Stack<(Color Color, float Width, List<PointF> Points)> _redo = [];
	public Color Ink { get; set; } = Color.FromArgb("#E5484D");
	public bool IsDrawing { get; private set; }
	public bool HasStrokes => _strokes.Count > 0;
	public bool CanUndo => HasStrokes && !IsDrawing;
	public bool CanRedo => _redo.Count > 0 && !IsDrawing;
	private float Fit => (float)Math.Min(Width / _pixelWidth, Height / _pixelHeight);

	public MarkupCanvas(int pixelWidth, int pixelHeight, Action changed)
	{
		(_pixelWidth, _pixelHeight, _changed) = (pixelWidth, pixelHeight, changed);
		Drawable = this;
		StartInteraction += (_, e) =>
		{
			if (!IsEnabled || IsDrawing || e.Touches.Length != 1 || Fit <= 0) { return; }
			_strokes.Add((Ink, 3 / Fit, [At(e.Touches[0])]));
			IsDrawing = true;
			Invalidate();
			_changed();
		};
		DragInteraction += (_, e) =>
		{
			if (!IsDrawing) { return; }
			if (e.Touches.Length != 1) { CancelStroke(); return; }
			_strokes[^1].Points.Add(At(e.Touches[0]));
			Invalidate();
		};
		EndInteraction += (_, e) =>
		{
			if (!IsDrawing) { return; }
			if (e.Touches.Length == 1) { _strokes[^1].Points.Add(At(e.Touches[0])); }
			IsDrawing = false;
			_redo.Clear();
			Invalidate();
			_changed();
		};
		CancelInteraction += (_, _) => CancelStroke();
		SizeChanged += (_, _) => { CancelStroke(); Invalidate(); };
	}

	private PointF At(PointF point)
	{
		float fit = Fit;
		return new PointF(Math.Clamp((point.X - ((float)Width - _pixelWidth * fit) / 2) / fit, 0, _pixelWidth),
			Math.Clamp((point.Y - ((float)Height - _pixelHeight * fit) / 2) / fit, 0, _pixelHeight));
	}

	private void CancelStroke()
	{
		if (!IsDrawing) { return; }
		_strokes.RemoveAt(_strokes.Count - 1);
		IsDrawing = false;
		Invalidate();
		_changed();
	}

	public void Undo()
	{
		if (!CanUndo) { return; }
		_redo.Push(_strokes[^1]);
		_strokes.RemoveAt(_strokes.Count - 1);
		Invalidate();
		_changed();
	}

	public void Redo()
	{
		if (!CanRedo) { return; }
		_strokes.Add(_redo.Pop());
		Invalidate();
		_changed();
	}

	public void Clear()
	{
		_strokes.Clear();
		_redo.Clear();
		IsDrawing = false;
		Invalidate();
		_changed();
	}

	public void Draw(ICanvas canvas, RectF dirtyRect)
	{
		float fit = Fit;
		if (fit <= 0) { return; }
		canvas.SaveState();
		canvas.Translate(((float)Width - _pixelWidth * fit) / 2, ((float)Height - _pixelHeight * fit) / 2);
		canvas.Scale(fit, fit);
		Paint(canvas);
		canvas.RestoreState();
	}

	// The same source-pixel drawing is used on screen and over the full-size bitmap during export.
	public void Paint(ICanvas canvas)
	{
		canvas.StrokeLineCap = LineCap.Round;
		foreach ((Color color, float width, List<PointF> points) in _strokes)
		{
			canvas.StrokeColor = canvas.FillColor = color;
			canvas.StrokeSize = width;
			if (points.Count == 1) { canvas.FillCircle(points[0].X, points[0].Y, width / 2); }
			for (int i = 1; i < points.Count; i++)
			{
				canvas.DrawLine(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);
			}
		}
	}
}
