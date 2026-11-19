using System.Windows;

namespace CaboCam.Views;

public partial class HostPromptDialog : Window
{
    public HostPromptDialog(string initial)
    {
        InitializeComponent();
        HostBox.Text = initial;
        Loaded += (_, _) => { HostBox.Focus(); HostBox.SelectAll(); };
    }

    public string Result => HostBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
