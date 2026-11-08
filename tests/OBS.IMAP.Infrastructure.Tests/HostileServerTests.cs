using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class HostileServerTests
{
    [Theory]
    [InlineData("* 1 FETCH (BODY[1] {2147483647}\r\n")]
    [InlineData("* 1 FETCH (BODYSTRUCTURE ((({0}\r\n(({0}\r\n(({0}\r\n(({0}\r\n(({0}\r\n")]
    public async Task Mailkit_parser_is_stopped_before_oversized_literal_or_deep_structure(string hostileResponse)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("* PREAUTH [CAPABILITY IMAP4rev1] fixture".AsMemory(), deadline.Token);
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var tag = line.Split(' ')[0];
                if (line.Contains("UID FETCH", StringComparison.Ordinal))
                {
                    // Send no literal payload. Allocation would be dangerous even before it arrives.
                    await writer.WriteAsync(hostileResponse.AsMemory(), deadline.Token);
                    return;
                }
                var response = line.Contains("EXAMINE", StringComparison.Ordinal)
                    ? $"* 1 EXISTS\r\n* FLAGS (\\Seen)\r\n* OK [UIDVALIDITY 42] valid\r\n{tag} OK [READ-ONLY] complete\r\n"
                    : $"* LIST (\\Inbox) \"/\" \"INBOX\"\r\n{tag} OK complete\r\n";
                await writer.WriteAsync(response.AsMemory(), deadline.Token);
            }
        }, deadline.Token);
        using var budget = new ProtocolBudget(1024 * 1024, 1024, 65536, 8);
        using var client = new ImapClient(budget);
        // Synthetic loopback protocol fixture, never a production plaintext mode.
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, deadline.Token);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, deadline.Token);
        var error = await Assert.ThrowsAsync<ConnectorException>(async () =>
        {
            await using var body = await client.Inbox.GetStreamAsync(new UniqueId(1), "1", 0, 16, deadline.Token);
        });
        Assert.Equal(ErrorCodes.LimitExceeded, error.Code);
        Assert.False(client.IsConnected);
        await server;
    }
}
