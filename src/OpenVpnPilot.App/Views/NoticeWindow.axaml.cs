using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenVpnPilot.App.ViewModels;

namespace OpenVpnPilot.App.Views;

public partial class NoticeWindow : Window
{
    public NoticeWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is NoticeViewModel notice)
        {
            notice.Acknowledged += (_, _) => Close();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
