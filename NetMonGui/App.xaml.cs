using System.Windows;
using Velopack;

namespace NetMonGui;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run first, before anything else touches the filesystem or UI - this is how
        // Velopack hooks the special install/update/uninstall lifecycle args the generated
        // Setup.exe launches the app with (e.g. on first run after install, after an update
        // is applied, before an uninstall). On a normal launch it does nothing and returns
        // immediately.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
