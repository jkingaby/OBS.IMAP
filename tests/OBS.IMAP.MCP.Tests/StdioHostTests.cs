using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.MCP.Tests;

public sealed class StdioHostTests
{
    [Fact]
    public async Task Production_stdio_binds_config_and_returns_only_locally_granted_accounts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"obs-imap-test-{Guid.NewGuid():N}.json");
        var options = new ConnectorOptions
        {
            Accounts = [
                new AccountOptions { Id = "allowed", Host = "imap.example.test", Username = "not-returned", CredentialReference = "env:UNUSED_TEST_SECRET" },
                new AccountOptions { Id = "hidden", Host = "imap.example.test", Username = "not-returned", CredentialReference = "env:UNUSED_TEST_SECRET", Writes = new() { Move = true } }
            ]
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Connector = options, Local = new { AllowedAccountIds = new[] { "allowed" } } }));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [typeof(Host.Program).Assembly.Location, "--config", path],
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            Assert.Equal(8, (await client.ListToolsAsync(cancellationToken: deadline.Token)).Count);
            var result = await client.CallToolAsync("list_accounts", cancellationToken: deadline.Token);
            Assert.NotEqual(true, result.IsError);
            var text = result.StructuredContent!.Value.ToString();
            Assert.Contains("allowed", text);
            Assert.DoesNotContain("hidden", text);
            Assert.DoesNotContain("not-returned", text);
            Assert.DoesNotContain("UNUSED_TEST_SECRET", text);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, true, ErrorCodes.ConfirmationRequired, false)]
    [InlineData(false, true, ErrorCodes.ConfirmationDeclined, false)]
    [InlineData(true, true, ErrorCodes.ProviderUnavailable, true)]
    [InlineData(null, false, ErrorCodes.ProviderUnavailable, true)]
    public async Task Write_tools_are_account_gated_and_send_confirmation_precedes_network_access(
        bool? approve, bool requireConfirmation, string expected, bool expectConnection)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var writable = new AccountOptions
        {
            Id = "write", Host = "imap.example.test", Username = "sender@example.test", CredentialReference = "env:UNUSED_TEST_SECRET",
            Writes = new()
            {
                Move = true, Trash = !requireConfirmation, CreateDraft = !requireConfirmation,
                Send = true, RequireSendConfirmation = requireConfirmation,
                Smtp = new()
                {
                    Host = "127.0.0.1", Port = port, Username = "fixture", CredentialReference = "env:UNUSED_TEST_SECRET",
                    AllowedPrivateNetworks = ["127.0.0.1/32"]
                }
            }
        };
        var options = new ConnectorOptions { Accounts = [writable, writable with { Id = "read", Writes = null }] };
        var path = Path.Combine(Path.GetTempPath(), $"obs-imap-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Connector = options }), token);
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Arguments = [typeof(Host.Program).Assembly.Location, "--config", path]
            });
            var prompted = false;
            var clientOptions = new McpClientOptions();
            if (approve is { } answer)
            {
                clientOptions = new McpClientOptions
                {
                    Capabilities = new() { Elicitation = new() { Form = new() } },
                    Handlers = new()
                    {
                        ElicitationHandler = (request, _) =>
                        {
                            Assert.False(listener.Pending());
                            Assert.Contains("sender@example.test", request!.Message);
                            Assert.Contains("recipient@example.test", request.Message);
                            Assert.Contains("Synthetic message", request.Message);
                            prompted = true;
                            return ValueTask.FromResult(new ElicitResult
                            {
                                Action = "accept",
                                Content = new Dictionary<string, JsonElement> { ["send"] = JsonSerializer.SerializeToElement(answer) }
                            });
                        }
                    }
                };
            }
            await using var client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: token);
            var tools = await client.ListToolsAsync(cancellationToken: token);
            Assert.Equal(requireConfirmation ? 10 : 12, tools.Count);
            Assert.Contains(tools, tool => tool.Name == "send_message" && tool.ProtocolTool.Annotations?.ReadOnlyHint == false);
            Assert.Contains(tools, tool => tool.Name == "move_message");
            Assert.Equal(!requireConfirmation, tools.Any(tool => tool.Name == "trash_message"));
            Assert.Equal(!requireConfirmation, tools.Any(tool => tool.Name == "create_draft"));

            var arguments = new Dictionary<string, object?>
            {
                ["accountId"] = "read",
                ["message"] = new { to = new[] { "recipient@example.test" }, subject = "Synthetic subject", body = "Synthetic message" }
            };
            var denied = await client.CallToolAsync("send_message", arguments, cancellationToken: token);
            Assert.Equal(ErrorCodes.AccessDenied, ErrorCode(denied));
            if (!requireConfirmation)
            {
                var deniedDraft = await client.CallToolAsync("create_draft", arguments, cancellationToken: token);
                Assert.Equal(ErrorCodes.AccessDenied, ErrorCode(deniedDraft));
                var deniedTrash = await client.CallToolAsync("trash_message", new Dictionary<string, object?>
                {
                    ["message"] = new MessageReference("read", "INBOX", 42, 7)
                }, cancellationToken: token);
                Assert.Equal(ErrorCodes.AccessDenied, ErrorCode(deniedTrash));
            }
            Assert.False(prompted);
            Assert.False(listener.Pending());

            arguments["accountId"] = "write";
            var call = client.CallToolAsync("send_message", arguments, cancellationToken: token).AsTask();
            if (expectConnection)
            {
                // Close before TLS to exercise the send gate without transmitting any mail.
                using var connection = await listener.AcceptTcpClientAsync(token);
            }
            Assert.Equal(expected, ErrorCode(await call));
            Assert.Equal(approve.HasValue && requireConfirmation, prompted);
            Assert.False(listener.Pending());
        }
        finally { File.Delete(path); }
    }

    private static string? ErrorCode(CallToolResult result)
    {
        Assert.True(result.IsError);
        return result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString();
    }
}
