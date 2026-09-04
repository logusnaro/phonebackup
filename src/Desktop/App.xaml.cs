using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PhoneBackup.Desktop;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private static readonly string StartupLogPath = Path.Combine(Path.GetTempPath(), "PhoneBackup-startup.log");
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "PhoneBackup-crash.log");
    private Mutex? _instanceMutex;

    private static void LogStartup(string message)
    {
        try { File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { }
    }

    internal static void LogCrash(string source, Exception? exception)
    {
        try { File.AppendAllText(CrashLogPath, $"{DateTimeOffset.Now:O} [{source}] {exception}{Environment.NewLine}"); } catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            if (MainWindow is not null) MainWindow.Dispatcher.BeginInvoke(() =>
                MessageBox.Show(MainWindow, $"오류를 기록했습니다. 작업을 계속할 수 없는 경우 앱을 재시작하세요.\n\n{args.Exception.Message}\n\n로그: {CrashLogPath}", "업무폰 백업 오류", MessageBoxButton.OK, MessageBoxImage.Error));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { LogCrash("UnobservedTaskException", args.Exception); args.SetObserved(); };
        var created = false;
        _instanceMutex = new Mutex(true, "Local\\PhoneBackup.Manager.V2.SingleInstance", out created);
        if (!created)
        {
            MessageBox.Show("PB 파일 관리가 이미 실행 중입니다. 기존 창을 사용하세요.", "PB", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        LogStartup("OnStartup entered");
        try
        {
            var dataRoot = ResolveDataRoot();
            LogStartup($"data root: {dataRoot}");
            Services = new AppServices(dataRoot);
            LogStartup("services constructed");
            // SQLite 초기화가 화면 스레드를 막지 않도록 백그라운드에서 시작한다.
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
            LogCrash("Startup", ex);
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
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
