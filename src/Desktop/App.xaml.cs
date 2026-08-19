using System.IO;
using System.Windows;

namespace PhoneBackup.Desktop;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private static readonly string StartupLogPath = Path.Combine(Path.GetTempPath(), "PhoneBackup-startup.log");

    private static void LogStartup(string message)
    {
        try { File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LogStartup("OnStartup entered");
        try
        {
            var dataRoot = ResolveDataRoot();
            LogStartup($"data root: {dataRoot}");
            Services = new AppServices(dataRoot);
            LogStartup("services constructed");
            // Keep database/Kestrel startup off the WPF dispatcher so a slow
            // socket or SQLite initialization can never block window creation.
            await Task.Run(() => Services.StartAsync());
            LogStartup("services started");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            LogStartup("main window shown");
        }
        catch (Exception ex)
        {
            LogStartup($"startup failed: {ex}");
            var logPath = Path.Combine(Path.GetTempPath(), "PhoneBackup-startup-error.log");
            await File.WriteAllTextAsync(logPath, ex.ToString());
            MessageBox.Show($"업무폰 백업 앱을 시작하지 못했습니다.\n\n오류 기록: {logPath}\n\n{ex.Message}", "시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("PHONEBACKUP_DATA_ROOT");
        var candidates = new[]
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBackup"),
            Path.Combine(AppContext.BaseDirectory, "PhoneBackupData"),
            Path.Combine(Path.GetTempPath(), "PhoneBackup")
        };

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                Directory.CreateDirectory(candidate!);
                var probe = Path.Combine(candidate!, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate!;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        throw new IOException("업무폰 백업 앱이 사용할 수 있는 데이터 폴더를 찾지 못했습니다.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        base.OnExit(e);
    }
}
