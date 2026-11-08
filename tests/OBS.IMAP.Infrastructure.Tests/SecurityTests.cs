using System.Net;
using System.Text;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class SecurityTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("100.64.0.1")]
    [InlineData("fec0::1")]
    [InlineData("4000::1")]
    public void Network_policy_denies_internal_special_and_mapped_addresses(string address) =>
        Assert.False(NetworkPolicy.IsAllowed(IPAddress.Parse(address), []));

    [Fact]
    public void Private_exception_is_narrow_and_public_addresses_work()
    {
        Assert.True(NetworkPolicy.IsAllowed(IPAddress.Parse("10.1.2.3"), ["10.1.2.0/24"]));
        Assert.False(NetworkPolicy.IsAllowed(IPAddress.Parse("10.1.3.3"), ["10.1.2.0/24"]));
        Assert.True(NetworkPolicy.IsAllowed(IPAddress.Parse("1.1.1.1"), []));
        Assert.True(NetworkPolicy.IsAllowed(IPAddress.Parse("2606:4700:4700::1111"), []));
    }

    [Fact]
    public void Cursor_binds_principal_scope_filter_and_expiry_and_rejects_tampering()
    {
        using var codec = new CursorCodec();
        var state = new CursorState("alice", "account", "INBOX", "filter", 42, 100, 90, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        var token = codec.Encode(state);
        Assert.Equal(state, codec.Decode(token, "alice", "account", "INBOX", "filter"));
        foreach (var action in new Action[]
        {
            () => codec.Decode(token, "bob", "account", "INBOX", "filter"),
            () => codec.Decode(token, "alice", "other", "INBOX", "filter"),
            () => codec.Decode(token, "alice", "account", "Sent", "filter"),
            () => codec.Decode(token, "alice", "account", "INBOX", "changed"),
            () => codec.Decode("A" + token[1..], "alice", "account", "INBOX", "filter"),
            () => codec.Decode(codec.Encode(state with { Expires = 1 }), "alice", "account", "INBOX", "filter")
        }) Assert.Equal(ErrorCodes.InvalidCursor, Assert.Throws<ConnectorException>(action).Code);
        using var afterRestart = new CursorCodec();
        Assert.Throws<ConnectorException>(() => afterRestart.Decode(token, "alice", "account", "INBOX", "filter"));
    }

    [Fact]
    public void Protocol_budget_rejects_huge_literals_before_their_content_arrives()
    {
        using var budget = new ProtocolBudget(1024, 128);
        Feed(budget, "* 1 FETCH (BODY[] {999999999}\r");
        Assert.Equal(ErrorCodes.LimitExceeded, Assert.Throws<ConnectorException>(() => Feed(budget, "\n")).Code);
    }

    [Fact]
    public void Protocol_budget_handles_split_markers_and_skips_literal_syntax()
    {
        using var budget = new ProtocolBudget(1024, 128, 64, 4);
        Feed(budget, "* 1 FETCH (BODY[] {8");
        Feed(budget, "}\r\n(((((((\n)\r\n");
        Feed(budget, "A OK done\r\n");
    }

    [Fact]
    public void Protocol_budget_caps_lines_depth_total_bytes_and_literal_aggregate()
    {
        using var line = new ProtocolBudget(1024, 128, 32);
        Assert.Throws<ConnectorException>(() => Feed(line, new string('a', 33)));
        using var depth = new ProtocolBudget(1024, 128, 64, 3);
        Assert.Throws<ConnectorException>(() => Feed(depth, "* X (((("));
        using var bytes = new ProtocolBudget(10, 8);
        Assert.Throws<ConnectorException>(() => Feed(bytes, "12345678901"));
        using var aggregate = new ProtocolBudget(1024, 8);
        Feed(aggregate, "* X {5}\r\n12345\r\n");
        Assert.Throws<ConnectorException>(() => Feed(aggregate, "* X {5}\r\n"));
    }

    [Fact]
    public void Zero_length_literal_cannot_reset_nested_response_depth()
    {
        using var budget = new ProtocolBudget(65536, 128, 1024, 4);
        for (var i = 0; i < 4; i++) Feed(budget, "({0}\r\n");
        Assert.Throws<ConnectorException>(() => Feed(budget, "({0}\r\n"));
    }

    [Fact]
    public async Task Credential_errors_never_include_secret_reference_or_contents()
    {
        var variable = "OBS_IMAP_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "sentinel-super-secret");
        try
        {
            var provider = new CredentialProvider(new ConnectorOptions());
            Assert.Equal("sentinel-super-secret", await provider.GetCredentialAsync("env:" + variable));
            var missing = await Assert.ThrowsAsync<ConnectorException>(async () => await provider.GetCredentialAsync("env:" + variable + "_MISSING"));
            Assert.DoesNotContain(variable, missing.ToString());
            Assert.DoesNotContain("sentinel-super-secret", missing.ToString());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task File_credentials_reject_traversal_before_opening()
    {
        var provider = new CredentialProvider(new ConnectorOptions
        {
            SecretRoot = Path.GetTempPath(),
            SecretFiles = new Dictionary<string, string> { ["escape"] = "../outside" }
        });
        Assert.Equal(ErrorCodes.InvalidConfiguration, (await Assert.ThrowsAsync<ConnectorException>(async () =>
            await provider.GetCredentialAsync("file:escape"))).Code);
    }

    [Fact]
    public async Task Unauthorized_account_cannot_trigger_network_or_credentials()
    {
        await using var service = new MailReadService(new ConnectorOptions
        {
            Accounts = [new AccountOptions { Id = "private", Host = "imap.example.net", Username = "user", CredentialReference = "env:UNSET" }]
        });
        var identity = new RequestIdentity("test", "alice", []);
        Assert.Empty(await service.ListAccountsAsync(identity));
        Assert.Equal(ErrorCodes.AccessDenied, (await Assert.ThrowsAsync<ConnectorException>(() =>
            service.GetFolderInfoAsync(identity, "private", "INBOX"))).Code);
    }

    private static void Feed(ProtocolBudget budget, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        budget.LogServer(bytes, 0, bytes.Length);
    }
}
