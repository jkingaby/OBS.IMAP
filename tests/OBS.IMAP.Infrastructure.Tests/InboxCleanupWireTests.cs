using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class InboxCleanupWireTests
{
    [Fact]
    public async Task Native_move_uses_UID_command_without_store_copy_or_broad_expunge()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("* PREAUTH [CAPABILITY IMAP4rev1 MOVE UIDPLUS] synthetic fixture");
            while (await reader.ReadLineAsync(deadline.Token) is { } command)
            {
                commands.Add(command);
                var tag = command.Split(' ')[0];
                if (command.Contains("UID MOVE", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync($"{tag} OK [COPYUID 43 7 9] moved");
                    break;
                }
                if (command.Contains("SELECT", StringComparison.Ordinal))
                    await writer.WriteAsync($"* 1 EXISTS\r\n* FLAGS (\\Seen)\r\n* OK [UIDVALIDITY 42] valid\r\n{tag} OK [READ-WRITE] selected\r\n");
                else if (command.Contains("LIST", StringComparison.Ordinal))
                    await writer.WriteAsync($"* LIST (\\HasNoChildren) \"/\" \"INBOX\"\r\n* LIST (\\Trash \\HasNoChildren) \"/\" \"Trash\"\r\n{tag} OK listed\r\n");
                else
                    await writer.WriteLineAsync($"{tag} OK complete");
            }
        }, deadline.Token);
        using var client = new ImapClient();
        // Plaintext PREAUTH exists only in this synthetic loopback fixture.
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, deadline.Token);
        var trash = await client.GetFolderAsync("Trash", deadline.Token);
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite, deadline.Token);
        await MailReadService.MoveCleanupMatchesAsync(client.Inbox, trash, [new UniqueId(7)], 42, deadline.Token);
        await server;
        Assert.Single(commands, c => c.Contains("UID MOVE 7", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("STORE", StringComparison.Ordinal)
            || c.Contains("EXPUNGE", StringComparison.Ordinal) || c.Contains("UID COPY", StringComparison.Ordinal));
    }
}
