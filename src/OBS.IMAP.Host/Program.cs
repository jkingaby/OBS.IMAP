using OBS.IMAP.Core;

namespace OBS.IMAP.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            await Console.Out.WriteLineAsync("obs-imap --config <absolute-path> [--transport stdio|http]");
            await Console.Out.WriteLineAsync("obs-imap accounts --help  (local account setup and connection testing)");
            return 0;
        }
        try
        {
            if (args.FirstOrDefault() == "accounts") return await AccountCommands.RunAsync(args[1..]);
            var (configPath, transport) = ParseArguments(args);
            if (!Path.IsPathFullyQualified(configPath)) throw new ArgumentException();
            var file = new FileInfo(configPath);
            if (!file.Exists || file.Length > 1024 * 1024) throw new ArgumentException();
            var config = new ConfigurationBuilder().AddJsonFile(configPath, optional: false, reloadOnChange: false).Build();
            var connector = config.GetSection("Connector").Get<ConnectorOptions>() ?? throw new ArgumentException();
            ConfigurationValidator.Validate(connector);
            if (transport == "stdio")
            {
                // RunAsync disposes the host asynchronously, including IMAP session services.
                var host = ConnectorHost.CreateStdio(connector, config.GetSection("Local").Get<LocalOptions>() ?? new());
                await host.RunAsync();
            }
            else
            {
                var remote = config.GetSection("Remote").Get<RemoteOptions>() ?? throw new ArgumentException();
                await using var app = ConnectorHost.CreateHttp(connector, remote);
                await app.RunAsync();
            }
            return 0;
        }
        catch (PlatformNotSupportedException) when (args.FirstOrDefault() == "accounts")
        {
            await Console.Error.WriteLineAsync("Account password setup requires Windows Credential Manager. Use env: or file: on Linux and containers.");
            return 1;
        }
        catch (Exception) when (args.FirstOrDefault() == "accounts")
        {
            await Console.Error.WriteLineAsync("Account command failed. Run 'obs-imap accounts --help'; check the absolute configuration path, account ID and file permissions. Password setup requires an interactive terminal. An existing vault target requires update-password rather than add. No sensitive diagnostics are emitted.");
            return 1;
        }
        catch (Exception)
        {
            // Never render parser/provider exceptions: configuration can contain secret-bearing values.
            await Console.Error.WriteLineAsync("Connector startup or runtime failed. Check the configuration and deployment requirements. No sensitive diagnostics are emitted.");
            return 1;
        }
    }

    private static (string ConfigPath, string Transport) ParseArguments(string[] args)
    {
        string? path = null;
        var transport = "stdio";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || !seen.Add(args[i])) throw new ArgumentException();
            switch (args[i])
            {
                case "--config": path = args[i + 1]; break;
                case "--transport": transport = args[i + 1]; break;
                default: throw new ArgumentException();
            }
        }
        if (path is null || transport is not ("stdio" or "http")) throw new ArgumentException();
        return (path, transport);
    }
}
