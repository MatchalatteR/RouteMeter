using System.IO;
using System.Text.Json;
using System.Windows;

namespace RouteMeter.Windows;
public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--core-self-test")
        {
            try
            {
                using var client = new CoreClient();
                ProtocolChecks.Run();
                var sample = await client.Read(e.Args[1]);
                File.WriteAllText(e.Args[2], JsonSerializer.Serialize(new { valid = true, apps = sample.Apps.Count, program = sample.Program }));
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
