using System;
using System.Threading.Tasks;
using System.Windows;
using FModel.Mcp;

namespace FModel.McpHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _ = Task.Run(async () =>
        {
            try { await McpServerHost.RunAsync(args); }
            finally { app.Dispatcher.Invoke(app.Shutdown); }
        });
        app.Run();
    }
}
