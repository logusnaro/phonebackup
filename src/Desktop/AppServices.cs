using System.IO;
using PhoneBackup.Desktop.Services;

namespace PhoneBackup.Desktop;

public sealed class AppServices : IDisposable
{
    public string DataRoot { get; }
    public DatabaseService Database { get; }
    public SmartSwitchCatalogService Catalog { get; }
    public SmartSwitchService SmartSwitch { get; }
    public SmartSwitchDiscoveryService SmartSwitchDiscovery { get; }
    public DiagnosticsService Diagnostics { get; }

    public AppServices(string dataRoot)
    {
        DataRoot = dataRoot;
        Database = new DatabaseService(Path.Combine(dataRoot, "phonebackup.db"));
        Catalog = new SmartSwitchCatalogService(Database);
        SmartSwitch = new SmartSwitchService();
        SmartSwitchDiscovery = new SmartSwitchDiscoveryService(Database);
        Diagnostics = new DiagnosticsService(Database);
    }

    public async Task StartAsync()
    {
        await Database.InitializeAsync();
    }

    public void Dispose()
    {
        Database.Dispose();
    }
}
