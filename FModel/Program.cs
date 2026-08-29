using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FModel.Mcp;

namespace FModel;

/// <summary>Chooses the GUI or stdio host before WPF's XAML startup pipeline runs.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            var guiApp = new App();
            guiApp.InitializeComponent();
            guiApp.Run();
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _ = Task.Run(async () =>
        {
            try { await McpServerHost.RunAsync(args); }
            finally { app.Dispatcher.Invoke(app.Shutdown); }
        });
        app.Run();
    }
}
