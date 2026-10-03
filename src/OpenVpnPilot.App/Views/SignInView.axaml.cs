using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenVpnPilot.App.Views;

public partial class SignInView : UserControl
{
    /// <summary>
    /// False where the hosting window shows <see cref="SignInActions"/> in its own row of buttons.
    /// </summary>
    public static readonly StyledProperty<bool> ShowsActionsProperty =
        AvaloniaProperty.Register<SignInView, bool>(nameof(ShowsActions), defaultValue: true);

    public SignInView()
    {
        InitializeComponent();
    }

    public bool ShowsActions
    {
        get => GetValue(ShowsActionsProperty);
        set => SetValue(ShowsActionsProperty, value);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
