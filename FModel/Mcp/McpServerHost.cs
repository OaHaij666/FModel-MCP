using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FModel.Mcp;

public static class McpServerHost
{
    public static Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<FModelMcpRuntime>();
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<FModelMcpTools>();

        return builder.Build().RunAsync();
    }
}
