using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using OBS.IMAP.Core;
using OBS.IMAP.Host;
using Xunit;

namespace OBS.IMAP.MCP.Tests;

public sealed class RemoteHostTests
{
    [Fact]
    public void Missing_remote_authentication_configuration_fails_before_listening()
    {
        Assert.Throws<InvalidOperationException>(() => ConnectorHost.CreateHttp(new ConnectorOptions(), new RemoteOptions()));
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("crlf")]
    [InlineData("issuer-query")]
    [InlineData("issuer-fragment")]
    [InlineData("null-hosts")]
    [InlineData("null-origins")]
    [InlineData("null-grants")]
    [InlineData("null-grant")]
    [InlineData("null-account-ids")]
    public void Malformed_remote_configuration_is_rejected_safely(string mutation)
    {
        var options = new RemoteOptions
        {
            Issuer = "https://identity.example.test", Audience = "obs-imap", ResourceUrl = "https://mail.example.test/mcp", AllowedHosts = ["mail.example.test"]
        };
        switch (mutation)
        {
            case "quote": options.RequiredScope = "mail.read\" injected=\"x"; break;
            case "crlf": options.RequiredScope = "mail.read\r\nInjected: x"; break;
            case "issuer-query": options.Issuer += "?untrusted=true"; break;
            case "issuer-fragment": options.Issuer += "#fragment"; break;
            case "null-hosts": options.AllowedHosts = null!; break;
            case "null-origins": options.AllowedOrigins = null!; break;
            case "null-grants": options.Grants = null!; break;
            case "null-grant": options.Grants = [null!]; break;
            case "null-account-ids": options.Grants = [new RemoteGrant { Subject = "alice", AccountIds = null! }]; break;
        }
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task Anonymous_requests_receive_oauth_challenge_and_public_metadata_has_no_mailboxes()
    {
        await using var fixture = await RemoteFixture.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Address + "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata", response.Headers.WwwAuthenticate.ToString());
        var metadata = await client.GetStringAsync(fixture.Address + "/.well-known/oauth-protected-resource");
        Assert.Contains("https://mail.example.test/mcp", metadata);
        Assert.DoesNotContain("mailbox-a", metadata);
    }

    [Theory]
    [InlineData("wrong-audience", "alice", "mail.read", false, HttpStatusCode.Unauthorized)]
    [InlineData("obs-imap", "alice", "mail.read", true, HttpStatusCode.Unauthorized)]
    [InlineData("obs-imap", "alice", "other", false, HttpStatusCode.Forbidden)]
    [InlineData("obs-imap", "unknown", "mail.read", false, HttpStatusCode.Forbidden)]
    public async Task Invalid_tokens_or_permissions_cannot_reach_mail_service(string audience, string subject, string scope, bool expired, HttpStatusCode expected)
    {
        await using var fixture = await RemoteFixture.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Address + "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(audience, subject, scope, expired));
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Forbidden) Assert.Contains("insufficient_scope", response.Headers.WwwAuthenticate.ToString());
        Assert.Null(fixture.Service.LastIdentity);
    }

    [Theory]
    [InlineData("bad-signature")]
    [InlineData("unsigned")]
    [InlineData("hmac")]
    [InlineData("wrong-issuer")]
    public async Task Token_signature_algorithm_and_issuer_attacks_are_denied(string attack)
    {
        await using var fixture = await RemoteFixture.StartAsync();
        var token = fixture.Token("obs-imap", "alice", "mail.read", mode: attack);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Address + "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.Service.LastIdentity);
    }

    [Fact]
    public async Task Signed_jwt_selects_identity_and_shared_tools_over_http()
    {
        await using var fixture = await RemoteFixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(fixture.Address + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + fixture.Token("obs-imap", "alice", "mail.read") }
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Equal(8, tools.Count);
        Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint));
        var result = await client.CallToolAsync("list_accounts", cancellationToken: deadline.Token);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("alice", fixture.Service.LastIdentity?.Subject);
        Assert.Equal(["mailbox-a"], fixture.Service.LastIdentity!.AllowedAccounts);
        Assert.Contains("mailbox-a", result.StructuredContent!.Value.ToString());
    }

    [Fact]
    public async Task Concurrent_remote_principals_receive_separate_account_views()
    {
        await using var fixture = await RemoteFixture.StartAsync();
        async Task<string> Accounts(string subject)
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.Address + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + fixture.Token("obs-imap", subject, "mail.read") }
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            var result = await client.CallToolAsync("list_accounts", cancellationToken: timeout.Token);
            Assert.NotEqual(true, result.IsError);
            return result.StructuredContent!.Value.ToString();
        }
        var results = await Task.WhenAll(Accounts("alice"), Accounts("bob"));
        Assert.Contains("mailbox-a", results[0]);
        Assert.DoesNotContain("mailbox-b", results[0]);
        Assert.Contains("mailbox-b", results[1]);
        Assert.DoesNotContain("mailbox-a", results[1]);
    }

    [Theory]
    [InlineData("attacker.example", null, HttpStatusCode.BadRequest)]
    [InlineData("127.0.0.1", "https://attacker.example", HttpStatusCode.Forbidden)]
    public async Task Host_and_origin_are_validated_before_mail_access(string host, string? origin, HttpStatusCode expected)
    {
        await using var fixture = await RemoteFixture.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.Address + "/.well-known/oauth-protected-resource");
        request.Headers.Host = host;
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }
}

internal sealed class RemoteFixture : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication app;
    private readonly RSA rsa;
    private readonly RsaSecurityKey key;
    private RemoteFixture(Microsoft.AspNetCore.Builder.WebApplication app, RSA rsa, RsaSecurityKey key, string address, TestMailService service)
    {
        this.app = app; this.rsa = rsa; this.key = key; Address = address; Service = service;
    }
    public string Address { get; }
    public TestMailService Service { get; }

    public static async Task<RemoteFixture> StartAsync()
    {
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var remote = new RemoteOptions
        {
            Issuer = "https://identity.example.test",
            Audience = "obs-imap",
            ResourceUrl = "https://mail.example.test/mcp",
            Port = 0,
            AllowedHosts = ["127.0.0.1"],
            Grants = [new RemoteGrant { Subject = "alice", AccountIds = ["mailbox-a"] }, new RemoteGrant { Subject = "bob", AccountIds = ["mailbox-b"] }]
        };
        var options = new ConnectorOptions
        {
            Accounts = [
                new AccountOptions { Id = "mailbox-a", Host = "imap.example.test", Username = "fixture", CredentialReference = "env:UNUSED_TEST_CREDENTIAL", Writes = new() { Move = true } },
                new AccountOptions { Id = "mailbox-b", Host = "imap.example.test", Username = "fixture", CredentialReference = "env:UNUSED_TEST_CREDENTIAL" }
            ]
        };
        var service = new TestMailService();
        var app = ConnectorHost.CreateHttp(options, remote, jwt =>
        {
            jwt.Configuration = new OpenIdConnectConfiguration { Issuer = remote.Issuer };
            jwt.Configuration.SigningKeys.Add(key);
        }, service);
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        return new RemoteFixture(app, rsa, key, address, service);
    }

    public string Token(string audience, string subject, string scope, bool expired = false, string mode = "normal")
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
        Issuer = mode == "wrong-issuer" ? "https://attacker.example.test" : "https://identity.example.test",
        Audience = audience,
        Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("scope", scope)]),
        NotBefore = DateTime.UtcNow.AddMinutes(-10),
        Expires = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = mode == "unsigned" ? null : mode == "hmac"
            ? new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)) { KeyId = "test-key" }, SecurityAlgorithms.HmacSha256)
            : new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
        if (mode != "bad-signature") return token;
        var signatureStart = token.LastIndexOf('.') + 1;
        return token[..signatureStart] + (token[signatureStart] == 'a' ? 'b' : 'a') + token[(signatureStart + 1)..];
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        rsa.Dispose();
    }
}
