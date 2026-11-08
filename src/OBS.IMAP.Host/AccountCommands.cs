using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OBS.IMAP.Core;
using OBS.IMAP.Infrastructure;

namespace OBS.IMAP.Host;

public static class AccountCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is [] or ["--help"] or ["-h"])
        {
            Console.WriteLine("obs-imap accounts add --config <absolute-path>\nobs-imap accounts test <id> --config <absolute-path>\nobs-imap accounts update-password <id> --config <absolute-path>\nobs-imap accounts cleanup-inbox <id> --config <absolute-path> [--apply]");
            return 0;
        }
        var applyCleanup = args is ["cleanup-inbox", _, "--config", _, "--apply"];
        if (applyCleanup) args = args[..^1];
        var add = args is ["add", "--config", _];
        if (!add && args is not ["test" or "update-password" or "cleanup-inbox", _, "--config", _])
            throw new ArgumentException();
        var path = args[^1];
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException();
        var exists = File.Exists(path);
        if (!exists && !add) throw new ArgumentException();
        if (exists && new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException();
        var root = exists ? JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject() : new JsonObject();
        var section = Property(root, "Connector") as JsonObject;
        if (section is null) { section = new JsonObject(); root["Connector"] = section; }
        var options = Bind(root);
        ConfigurationValidator.Validate(options);
        if (add)
        {
            RequireInteractive();
            var id = Prompt("Account ID");
            InputValidator.AccountId(id);
            if (options.Accounts.Any(a => a.Id == id)) throw new ArgumentException();
            var account = new AccountOptions
            {
                Id = id, DisplayName = Prompt("Display name"), Host = Prompt("IMAP hostname"),
                Port = int.Parse(Prompt("Port (993 for implicit TLS, 143 for STARTTLS)")),
                Security = Prompt("TLS mode (SslOnConnect or StartTls)") switch
                {
                    "SslOnConnect" => ImapSecurityMode.SslOnConnect,
                    "StartTls" => ImapSecurityMode.StartTls,
                    _ => throw new ArgumentException()
                },
                Username = Prompt("Username"), CredentialReference = "wincred:obs-imap/" + id
            };
            var updated = options with { Accounts = [.. options.Accounts, account] };
            ConfigurationValidator.Validate(updated);
            WindowsCredentialStore.ValidateTarget(account.CredentialReference[8..]);
            WindowsCredentialStore.Write(account.CredentialReference[8..], account.Username, Password(updated.Limits.MaxSecretBytes), overwrite: false);
            var accounts = Property(section, "Accounts") as JsonArray;
            if (accounts is null) { accounts = new JsonArray(); section["Accounts"] = accounts; }
            accounts.Add(JsonSerializer.SerializeToNode(account, JsonOptions));
            // Existing explicit local permissions and remote grants are never broadened.
            await SaveAsync(path, root);
            Console.WriteLine("Account saved. If Local.AllowedAccountIds is explicit, add this ID to grant local access. Restart the connector after configuration changes.");
            return await TestAsync(updated, account);
        }
        InputValidator.AccountId(args[1]);
        var selected = options.Accounts.SingleOrDefault(a => a.Id == args[1]) ?? throw new ArgumentException();
        if (args[0] == "cleanup-inbox")
        {
            var local = new ConfigurationBuilder().AddJsonFile(path).Build().GetSection("Local").Get<LocalOptions>() ?? new();
            var identity = new RequestIdentity("local-cleanup", "operator", local.AllowedAccountIds ?? options.Accounts.Select(a => a.Id).ToArray());
            return await CleanupAsync(options, identity, selected.Id, applyCleanup);
        }
        if (args[0] == "update-password")
        {
            RequireInteractive();
            if (selected.Authentication != ImapAuthenticationMode.Password) throw new ArgumentException();
            var reference = "wincred:obs-imap/" + selected.Id;
            WindowsCredentialStore.ValidateTarget(reference[8..]);
            WindowsCredentialStore.Write(reference[8..], selected.Username, Password(options.Limits.MaxSecretBytes));
            var accounts = (JsonArray)Property(section, "Accounts")!;
            var index = options.Accounts.ToList().FindIndex(a => a.Id == selected.Id);
            var node = accounts[index]!.AsObject();
            var key = node.Select(p => p.Key).FirstOrDefault(k => k.Equals("CredentialReference", StringComparison.OrdinalIgnoreCase)) ?? "CredentialReference";
            node[key] = reference;
            await SaveAsync(path, root);
            Console.WriteLine(selected.CredentialReference == reference
                ? "Password saved. It will be read on the next IMAP connection; existing authenticated sessions may remain active until idle eviction."
                : "Password saved in Windows Credential Manager. Restart the connector to load the changed credential reference.");
            selected = selected with { CredentialReference = reference };
            options = options with { Accounts = options.Accounts.Select(a => a.Id == selected.Id ? selected : a).ToArray() };
        }
        return await TestAsync(options, selected);
    }

    private static JsonNode? Property(JsonObject node, string name) => node.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static async Task<int> CleanupAsync(ConnectorOptions options, RequestIdentity identity, string accountId, bool apply)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var service = new MailReadService(options);
        string? continuation = null;
        var matched = 0;
        var moved = 0;
        try
        {
            // Work and counts are bounded. The next hourly run resumes by rescanning the remaining inbox.
            for (var page = 0; page < 100; page++)
            {
                var result = await service.CleanupInboxAsync(identity, accountId, apply, continuation, deadline.Token);
                matched += result.Matched;
                moved += result.Moved;
                continuation = result.Continuation;
                if (continuation is null) break;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { mode = apply ? "apply" : "preview", matched, moved,
                complete = continuation is null }, JsonOptions));
            return 0;
        }
        catch (ConnectorException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Code, correlationId = error.CorrelationId,
                matched, moved, complete = false, outcomeMayBePartial = apply }, JsonOptions));
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ErrorCodes.Timeout, matched, moved,
                complete = false, outcomeMayBePartial = apply }, JsonOptions));
            return 1;
        }
    }
    private static ConnectorOptions Bind(JsonObject root) => new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString())))
        .Build().GetSection("Connector").Get<ConnectorOptions>() ?? new();

    private static async Task SaveAsync(string path, JsonObject root)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = root.ToJsonString(JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new ArgumentException();
            await File.WriteAllTextAsync(temporary, json);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<int> TestAsync(ConnectorOptions options, AccountOptions account)
    {
        if (account.CredentialReference.StartsWith("wincred:", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows Credential Manager requires Windows. Use env: or file: for Linux and containers.");
            return 1;
        }
        var provider = new CredentialProvider(options);
        try { _ = await provider.GetCredentialAsync(account.CredentialReference); }
        catch (PlatformNotSupportedException)
        {
            Console.Error.WriteLine("Windows Credential Manager requires Windows and the same Windows user as setup.");
            return 1;
        }
        catch (ConnectorException)
        {
            Console.Error.WriteLine("Stored credential is missing, empty, inaccessible, or invalid. Check the credential source under the user running the connector.");
            return 1;
        }
        try
        {
            await using var service = new MailReadService(options, provider);
            // Local operator diagnostic: use the same network, TLS, authorization and deadline controls as reads.
            await service.GetFolderInfoAsync(new RequestIdentity("local-setup", "operator", [account.Id]), account.Id, "INBOX");
            Console.WriteLine("Connection successful: TLS, authentication and read-only INBOX access verified. No message bodies retrieved.");
            return 0;
        }
        catch (ConnectorException exception)
        {
            Console.Error.WriteLine(exception.Code == ErrorCodes.AuthenticationFailed
                ? "IMAP login rejected. Check the username/password and whether the provider requires an app password or OAuth2."
                : $"Connection test failed: {exception.Code}; correlation_id={exception.CorrelationId}. Check network, TLS trust and endpoint policy.");
            return 1;
        }
    }

    private static void RequireInteractive()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new ArgumentException("Run setup from an interactive terminal.");
    }
    private static string Prompt(string label)
    {
        Console.Write(label + ": ");
        var value = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
        return value;
    }
    private static string Password(int maximumBytes)
    {
        Console.Write("Password or app password (hidden): ");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar))
            {
                if (value.Length >= 1280) throw new ArgumentException();
                value.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        var password = value.ToString();
        if (Encoding.UTF8.GetByteCount(password) > maximumBytes) throw new ArgumentException();
        return password;
    }
}
