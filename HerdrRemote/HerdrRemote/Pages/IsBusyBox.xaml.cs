namespace HerdrRemote.Pages;

public partial class IsBusyBox : ContentView
{
	// Set by UI handlers right before the box shows: the texts change at once (a posted update could show the previous title for a frame).
	public string BoxTitle
	{
		get => BusyTitle.Text;
		set => BusyTitle.Text = value;
	}

	public string BoxBody
	{
		get => BusyBody.Text;
		set => BusyBody.Text = value;
	}

	public IsBusyBox()
	{
		InitializeComponent();
	}
}
