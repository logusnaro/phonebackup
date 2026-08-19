using System.Windows;
using System.IO;
using System.Text.Json;
using PhoneBackup.Desktop.Models;

namespace PhoneBackup.Desktop;

public partial class PairingWindow : Window
{
    private readonly PairingTicket _ticket;
    public PairingWindow(PairingTicket ticket, string memberName)
    {
        InitializeComponent();
        _ticket = ticket;
        MemberText.Text = $"연결 대상 멤버: {memberName}";
        CodeText.Text = ticket.PairingCode;
        ExpiresText.Text = $"10분 후 만료 · USB 등록 파일을 저장한 뒤 Android 앱에서 가져오세요.";
    }
    private void SaveUsbFile_Click(object sender, RoutedEventArgs e)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBackup");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"PhoneBackup-pairing-{CodeText.Text}.json");
        var payload = new
        {
            version = 1,
            ticketId = _ticket.TicketId,
            // adb reverse maps the phone's localhost:42817 to this PC listener.
            serverUrl = "https://127.0.0.1:42817",
            certificateSha256 = _ticket.CertificateSha256
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show(this, $"USB 등록 파일을 저장했습니다.\n{path}\n\nUSB 연결 상태에서 Android 앱의 ‘USB 등록 가져오기’를 누르세요.", "PhoneBackup", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
