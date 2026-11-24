namespace Popups;

public partial class DisplayMsg : ContentView
{
    public event EventHandler<string?>? Button_Clicked;
    bool ShowAsModal = false;

    public DisplayMsg(string Title, string MessageToShow, string[]? Buttons, bool Destructive = false)
    {
        InitializeComponent();
        this.BindingContext = this;

        LblMessage.Text = MessageToShow;

        if (string.IsNullOrEmpty(Title) == false) Header_CenterTitle.Text = Title;
        PopupsHandle.SetButtons(ButtonLeft, ButtonRight, Buttons, ButtonThird);

        if (Destructive) PopupsHandle.MakeDestructive(ButtonLeft);

        ShowAsModal = !string.IsNullOrEmpty(ButtonLeft.Text);
    }

    // Dismiss externally with null (Cancel); Button_Clicked removes the popup before a replacement is added.
    public void Dismiss() => Button_Clicked?.Invoke(this, null);

    private void TouchOut_Tapped(object? sender, TappedEventArgs e)
    {
        if (ShowAsModal == false)
        {
            Button_Clicked?.Invoke(this, null);
        }
    }

    private void ButtonLeft_Clicked(object? sender, EventArgs e)
    {
        Button_Clicked?.Invoke(this, ButtonLeft.Text);
    }

    private void ButtonRight_Clicked(object? sender, EventArgs e)
    {
        Button_Clicked?.Invoke(this, ButtonRight.Text);
    }

    private void ButtonThird_Clicked(object? sender, EventArgs e)
    {
        Button_Clicked?.Invoke(this, ButtonThird.Text);
    }
}