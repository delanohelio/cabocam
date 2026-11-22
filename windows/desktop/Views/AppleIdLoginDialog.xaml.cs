using System.Windows;

namespace CaboCam.Views;

public partial class AppleIdLoginDialog : Window
{
    public AppleIdLoginDialog(string initialEmail)
    {
        InitializeComponent();
        EmailBox.Text = initialEmail;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(EmailBox.Text)) EmailBox.Focus();
            else PasswordBox.Focus();
        };
    }

    public string Email => EmailBox.Text.Trim();
    public string Password => PasswordBox.Password;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Email.Length == 0 || !Email.Contains('@') || Password.Length == 0) return;
        DialogResult = true;
    }
}
