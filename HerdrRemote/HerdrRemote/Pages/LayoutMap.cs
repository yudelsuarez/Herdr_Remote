using System.Text.Json.Nodes;
using HerdrRemote.Remote;

namespace HerdrRemote.Pages;

// Miniature tab layout mirrors herdr-app's weighted row/column splits through HerdrLayout.Map: missing or pixel
// sizes count as 1, hidden nodes are skipped, and stacks show the active child. Fill the selected piece; dim
// non-live terminals and mod panes. Proportional bounds match the star grid. The miniature takes no touch
// and is excluded from accessibility because its row is announced.
// The sheet's large map uses the same proportions, labels panes, and makes live pieces accessible buttons;
// dim pieces ignore taps. Show a label only if its inner bounds (less stroke and padding) fit a text line
// and at least "…". Measure both with an invisible, input-transparent, inaccessible probe in the same style;
// never estimate. Hiding a label must retain the piece's accessible name and tap action.
internal sealed class LayoutMap : AbsoluteLayout
{
	public LayoutMap(JsonObject root, IReadOnlySet<string> live, string? on)
		: this(HerdrLayout.Map(root), live, on) { }

	public LayoutMap(IReadOnlyList<LayoutCell> cells, IReadOnlySet<string> live, string? on)
	{
		Style = App.Res<Style>("Map");
		InputTransparent = true;
		AutomationProperties.SetIsInAccessibleTree(this, false);
		foreach (LayoutCell cell in cells)
		{
			string style = cell.Terminal is { } address && live.Contains(address) ? address == on ? "Map.CellOn" : "Map.Cell" : "Map.CellDim";
			Border piece = new() { Style = App.Res<Style>(style) };
			Place(piece, cell);
		}
	}

	// The large map: name(address) is a live piece's label; tapped gets the address of the live piece tapped.
	public LayoutMap(JsonObject root, IReadOnlySet<string> live, string on, Func<string, string> name, Action<string> tapped)
	{
		Style = App.Res<Style>("Map.Large");
		Label probe = new() { Style = App.Res<Style>("Map.PieceText"), Text = "…", Opacity = 0, InputTransparent = true };
		AutomationProperties.SetIsInAccessibleTree(probe, false);
		SetLayoutBounds((BindableObject)probe, new Rect(0, 0, AutoSize, AutoSize));
		Add(probe);
		List<(Border Piece, Label Text)> labelled = [];
		void Fit(Border piece, Label text)
		{
			double width = piece.Width - 2 * piece.StrokeThickness - piece.Padding.HorizontalThickness;
			double height = piece.Height - 2 * piece.StrokeThickness - piece.Padding.VerticalThickness;
			text.IsVisible = probe.Width <= 0 || probe.Height <= 0 || piece.Width < 0 || (width >= probe.Width && height >= probe.Height);
		}
		probe.SizeChanged += (_, _) => labelled.ForEach(l => Fit(l.Piece, l.Text));
		foreach (LayoutCell cell in HerdrLayout.Map(root))
		{
			bool isLive = cell.Terminal is { } address && live.Contains(address);
			bool isOn = isLive && cell.Terminal == on;
			Border piece = new() { Style = App.Res<Style>(isOn ? "Map.PieceOn" : isLive ? "Map.Piece" : "Map.PieceDim") };
			if (isLive)
			{
				string pane = cell.Terminal!;
				string label = name(pane);
				Label text = new() { Style = App.Res<Style>(isOn ? "Map.PieceTextOn" : "Map.PieceText"), Text = label };
				piece.Content = text;
				labelled.Add((piece, text));
				piece.SizeChanged += (_, _) => Fit(piece, text);
				SemanticProperties.SetDescription(piece, label);
				AutomationProperties.SetIsInAccessibleTree(piece, true);
				TapGestureRecognizer tap = new();
				tap.Tapped += (_, _) => tapped(pane);
				piece.GestureRecognizers.Add(tap);
			}
			else
			{
				piece.InputTransparent = true;
				AutomationProperties.SetIsInAccessibleTree(piece, false);
			}
			Place(piece, cell);
		}
	}

	private void Place(Border piece, LayoutCell cell)
	{
		SetLayoutFlags((BindableObject)piece, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
		SetLayoutBounds((BindableObject)piece, new Rect(cell.Width >= 1 ? 0 : cell.X / (1 - cell.Width), cell.Height >= 1 ? 0 : cell.Y / (1 - cell.Height), cell.Width, cell.Height));
		Add(piece);
	}

	// How many pieces the map of `root` has (a tab with more than one shows a map, else its address).
	public static int Pieces(JsonObject root) => HerdrLayout.Map(root).Count;
}
