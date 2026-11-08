using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace OBS.IMAP.Feasibility.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Stdio_process_initializes_discovers_and_calls_shared_tool()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // SDK project references copy the runtimeconfig and deps files beside the probe assembly.
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [typeof(Program).Assembly.Location, "--transport", "stdio"],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        await AssertProbeAsync(client, deadline.Token);
    }

    [Fact]
    public async Task Loopback_streamable_http_initializes_discovers_and_calls_same_tool()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var app = Program.CreateHttpProbe();
        await app.StartAsync(deadline.Token);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var address = Assert.Single(addresses.Addresses);
            Assert.StartsWith("http://127.0.0.1:", address);
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(address + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            await AssertProbeAsync(client, deadline.Token);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertProbeAsync(McpClient client, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        Assert.Equal("probe", Assert.Single(tools).Name);
        var result = await client.CallToolAsync("probe", cancellationToken: cancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("obs-imap-feasibility", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
