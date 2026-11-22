using System.Linq;
using System.Windows;

namespace CaboCam.Views;

public partial class TwoFactorDialog : Window
{
    public TwoFactorDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => CodeBox.Focus();
    }

    public string Code => new(CodeBox.Text.Where(char.IsDigit).ToArray());

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Code.Length != 6) return;
        DialogResult = true;
    }
}
