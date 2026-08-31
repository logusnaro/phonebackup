using System.IO;
using PhoneBackup.Desktop.Models;
using PhoneBackup.Desktop.Services;

namespace PhoneBackup.Desktop;

public sealed class AppServices : IDisposable
{
    public string DataRoot { get; }
    public DatabaseService Database { get; }
    public BackupService Backups { get; }
    public SmartSwitchImportService SmartSwitchImport { get; }
    public SmartSwitchService SmartSwitch { get; }
    public ContactService Contacts { get; }
    public ScheduleService Schedules { get; }
    public PairingService Pairing { get; }
    public PairingDiscoveryService PairingDiscovery { get; }
    public LocalServer Server { get; }
    public DiagnosticsService Diagnostics { get; }

    public AppServices(string dataRoot)
    {
        DataRoot = dataRoot;
        Database = new DatabaseService(Path.Combine(dataRoot, "phonebackup.db"));
        Backups = new BackupService(Database);
        SmartSwitchImport = new SmartSwitchImportService(Database, Backups);
        SmartSwitch = new SmartSwitchService(Database);
        Contacts = new ContactService(Database);
        Schedules = new ScheduleService(Database);
        Pairing = new PairingService(Database);
        PairingDiscovery = new PairingDiscoveryService(Pairing);
        Server = new LocalServer(Database, Backups, SmartSwitch, Pairing, dataRoot);
        Diagnostics = new DiagnosticsService(Database);
    }

    public async Task StartAsync()
    {
        await Database.InitializeAsync();
        await Backups.LoadConfiguredRootAsync();
        await Server.StartAsync();
        PairingDiscovery.Start();
        Schedules.Start();
    }

    public void Dispose()
    {
        Server.Dispose();
        PairingDiscovery.Dispose();
        Schedules.Dispose();
        Database.Dispose();
    }
}
