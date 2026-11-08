using System.ComponentModel;
using System.Net;
using ModelContextProtocol.Server;

namespace OBS.IMAP.Feasibility;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // This isolated probe accepts no configuration or mailbox credentials.
        if (args is not ["--transport", "stdio"])
        {
            await Console.Error.WriteLineAsync("Feasibility probe: use --transport stdio. HTTP is available only through the test harness.");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            DisableDefaults = true
        });
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<ProbeTools>();
        await builder.Build().RunAsync();
        return 0;
    }

    public static WebApplication CreateHttpProbe()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["AllowedHosts"] = "127.0.0.1";
        // Bind an ephemeral IPv4 loopback port, regardless of environment URL overrides.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<ProbeTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }
}

[McpServerToolType]
public sealed class ProbeTools
{
    [McpServerTool(Name = "probe", ReadOnly = true)]
    [Description("Returns a fixed diagnostic value. No mailbox or credential access.")]
    public static string Probe() => "obs-imap-feasibility";
}
