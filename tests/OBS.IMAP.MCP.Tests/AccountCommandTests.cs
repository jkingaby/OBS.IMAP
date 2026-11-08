using System.Diagnostics;
using System.Text.Json;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.MCP.Tests;

public sealed class AccountCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_requires_explicit_optin_before_any_credential_or_network_access(bool apply)
    {
        var path = Path.Combine(Path.GetTempPath(), $"obs-imap-cleanup-{Guid.NewGuid():N}.json");
        var options = new ConnectorOptions { Accounts = [new AccountOptions
        {
            Id = "test", Host = "imap.example.test", Username = "synthetic", CredentialReference = "env:UNUSED_CLEANUP_SECRET"
        }] };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Connector = options }));
        try
        {
            string[] args = ["accounts", "cleanup-inbox", "test", "--config", path];
            if (apply) args = [.. args, "--apply"];
            var result = await RunAsync(args);
            Assert.Equal(1, result.Exit);
            Assert.Contains("access_denied", result.Error);
            Assert.DoesNotContain("UNUSED_CLEANUP_SECRET", result.Error + result.Output);
            Assert.Contains("\"moved\": 0", result.Error);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Missing_credential_test_is_actionable_and_does_not_disclose_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"obs-imap-cli-{Guid.NewGuid():N}.json");
        var sentinel = "PRIVATE_USERNAME_SENTINEL";
        var options = new ConnectorOptions { Accounts = [new AccountOptions
        {
            Id = "test", Host = "imap.example.test", Username = sentinel,
            CredentialReference = "env:OBS_IMAP_MISSING_" + Guid.NewGuid().ToString("N")
        }] };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Connector = options }));
        try
        {
            var result = await RunAsync("accounts", "test", "test", "--config", path);
            Assert.Equal(1, result.Exit);
            Assert.Contains("Stored credential is missing", result.Error);
            Assert.DoesNotContain(sentinel, result.Output + result.Error);
            Assert.DoesNotContain("imap.example.test", result.Output + result.Error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Setup_rejects_redirected_password_input_without_creating_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"obs-imap-cli-{Guid.NewGuid():N}.json");
        var result = await RunAsync("accounts", "add", "--config", path);
        Assert.Equal(1, result.Exit);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Setup_help_is_separate_from_mcp_host_startup(bool explicitHelp)
    {
        var result = await RunAsync(explicitHelp ? ["accounts", "--help"] : ["accounts"]);
        Assert.Equal(0, result.Exit);
        Assert.Contains("update-password", result.Output);
        Assert.Empty(result.Error);
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Host.Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
