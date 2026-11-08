using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class ReadDiscoveryWireTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 57)]
    [InlineData(2, true, 59)]
    public async Task Personal_namespace_listing_and_uid_boundary_use_bounded_read_commands(
        int count, bool hasUidNext, uint expectedUpper)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            var examineCount = 0;
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("* PREAUTH [CAPABILITY IMAP4rev1 NAMESPACE] fixture");
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var command = line[(line.IndexOf(' ') + 1)..];
                commands.Add(command);
                var response = command.Split(' ')[0] switch
                {
                    "NAMESPACE" => "* NAMESPACE ((\"\" \"/\")(\"Personal/\" \"/\")) NIL NIL\r\n",
                    "LIST" when command.Contains("Personal/*", StringComparison.Ordinal) => "* LIST () \"/\" \"Personal/Archive\"\r\n",
                    "LIST" when command.Contains('*') => "* LIST () \"/\" \"INBOX\"\r\n* LIST () \"/\" \"Personal/Archive\"\r\n",
                    "LIST" => "* LIST () \"/\" \"INBOX\"\r\n",
                    "EXAMINE" => $"* {count} EXISTS\r\n* FLAGS (\\Seen)\r\n* OK [UIDVALIDITY 42] valid\r\n"
                        + (hasUidNext ? $"* OK [UIDNEXT {59 + ++examineCount}] next\r\n" : ""),
                    "FETCH" => "* 2 FETCH (UID 57)\r\n",
                    "LOGOUT" => "* BYE complete\r\n",
                    _ => ""
                };
                var tag = line[..line.IndexOf(' ')];
                await writer.WriteAsync($"{response}{tag} OK [READ-ONLY] complete\r\n");
                if (command == "LOGOUT") break;
            }
        }, deadline.Token);

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, deadline.Token);
        var folders = await MailReadService.ListPersonalFoldersAsync(client, deadline.Token);
        Assert.Equal(new[] { "INBOX", "Personal/Archive" }, folders.Select(folder => folder.FullName).Order(StringComparer.Ordinal));
        var inbox = await MailReadService.OpenAsync(client, "INBOX", deadline.Token);
        Assert.Equal(expectedUpper, await MailReadService.ReadSearchUpperUidAsync(inbox, deadline.Token));
        if (hasUidNext)
        {
            inbox = await MailReadService.OpenAsync(client, "INBOX", deadline.Token, refresh: true);
            Assert.Equal(expectedUpper + 1, await MailReadService.ReadSearchUpperUidAsync(inbox, deadline.Token));
        }
        await client.DisconnectAsync(true, deadline.Token);
        await server;

        Assert.Contains(commands, command => command.StartsWith("LIST ", StringComparison.Ordinal) && command.Contains("Personal/*", StringComparison.Ordinal));
        Assert.Equal(count > 0 && !hasUidNext ? ["FETCH 2 UID"] : Array.Empty<string>(),
            commands.Where(command => command.StartsWith("FETCH ", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, command => command.StartsWith("SELECT ", StringComparison.Ordinal));
    }
}
