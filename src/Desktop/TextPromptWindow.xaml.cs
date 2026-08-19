using System.Windows;

namespace PhoneBackup.Desktop;

public partial class TextPromptWindow : Window
{
    public string Value => ValueBox.Text;
    public TextPromptWindow(string message, string title)
    { InitializeComponent(); Title = title; MessageText.Text = message; }
    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}
