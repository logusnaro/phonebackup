using System.Diagnostics;
using System.IO;

namespace PhoneBackup.Desktop.Services;

public sealed class SmartSwitchService
{
    public bool TryOpenApplication(out string message)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Samsung", "Smart Switch PC", "SmartSwitchPC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Samsung", "Smart Switch PC", "SmartSwitchPC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Samsung", "Smart Switch PC", "SmartSwitchPC.exe")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            message = "Smart Switch PC를 찾지 못했습니다. Samsung Smart Switch PC를 먼저 설치해 주세요.";
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            message = "Smart Switch를 열었습니다. 백업 완료 후 PB에서 ‘자동 찾기’를 누르세요.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Smart Switch를 열지 못했습니다: {ex.Message}";
            return false;
        }
    }
}
