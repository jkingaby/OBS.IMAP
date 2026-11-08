using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Xunit;

namespace OBS.IMAP.Feasibility.Tests;

public sealed class ImapProbeTests
{
    [Fact]
    public async Task Uid_search_and_partial_fetch_use_read_only_peek_commands()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var commands = new ConcurrentQueue<string>();
        var server = ServeReadSessionAsync(listener, commands, deadline.Token);
        using var client = new ImapClient();
        // Plaintext is restricted to this synthetic protocol fixture; never a production option.
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            SecureSocketOptions.None, deadline.Token);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, deadline.Token);
        Assert.Equal(42u, client.Inbox.UidValidity);
        var query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(1), new UniqueId(100)))
            .And(SearchQuery.SubjectContains("fixture"));
        var matches = await client.Inbox.SearchAsync(query, deadline.Token);
        Assert.Equal(7u, Assert.Single(matches).Id);
        await using var body = await client.Inbox.GetStreamAsync(new UniqueId(7), "1", 0, 4, deadline.Token);
        using var text = new StreamReader(body);
        Assert.Equal("test", await text.ReadToEndAsync(deadline.Token));
        await client.DisconnectAsync(true, deadline.Token);
        await server;
        Assert.Contains(commands, command => command.Contains("EXAMINE", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("UID SEARCH", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("BODY.PEEK[1]<0.4>", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains(" STORE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_greeting_read_terminates_connection_attempt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new ImapClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(listener.LocalEndpoint, deadline.Token);
        using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
        var connecting = client.ConnectAsync(socket, "imap.example.test", ((IPEndPoint)listener.LocalEndpoint).Port,
            SecureSocketOptions.None, deadline.Token);
        await deadline.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Pinned_socket_keeps_original_tls_hostname_and_rejects_untrusted_certificate()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=imap.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("imap.example.test");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string? observedSni = null;
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var tls = new SslStream(accepted.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) =>
                    {
                        observedSni = name;
                        return certificate;
                    }
                }, deadline.Token);
            }
            catch (System.Security.Authentication.AuthenticationException) { /* The client must reject the untrusted certificate. */ }
            catch (IOException) { /* The peer may close without sending a TLS alert. */ }
        }, deadline.Token);
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(listener.LocalEndpoint, deadline.Token);
        using var client = new ImapClient();
        await Assert.ThrowsAsync<SslHandshakeException>(() => client.ConnectAsync(socket,
            "imap.example.test", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.SslOnConnect, deadline.Token));
        await server;
        Assert.Equal("imap.example.test", observedSni);
        Assert.False(client.IsConnected);
    }

    private static async Task ServeReadSessionAsync(TcpListener listener, ConcurrentQueue<string> commands, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("* PREAUTH [CAPABILITY IMAP4rev1] fixture".AsMemory(), token);
        while (await reader.ReadLineAsync(token) is { } line)
        {
            commands.Enqueue(line);
            var tag = line.Split(' ')[0];
            if (line.Contains("LOGOUT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync($"* BYE fixture\r\n{tag} OK logout".AsMemory(), token);
                return;
            }
            var data = line.Contains("EXAMINE", StringComparison.Ordinal)
                ? "* 1 EXISTS\r\n* FLAGS (\\Seen)\r\n* OK [UIDVALIDITY 42] valid\r\n* OK [UIDNEXT 8] next\r\n"
                : line.Contains("UID SEARCH", StringComparison.Ordinal) ? "* SEARCH 7\r\n"
                : line.Contains("UID FETCH", StringComparison.Ordinal) ? "* 1 FETCH (UID 7 BODY[1]<0> {4}\r\ntest)\r\n"
                : line.Contains("LIST", StringComparison.Ordinal) ? "* LIST (\\Inbox) \"/\" \"INBOX\"\r\n"
                : line.Contains("CAPABILITY", StringComparison.Ordinal) ? "* CAPABILITY IMAP4rev1\r\n" : "";
            var status = line.Contains("EXAMINE", StringComparison.Ordinal) ? "[READ-ONLY] complete" : "complete";
            await writer.WriteAsync($"{data}{tag} OK {status}\r\n".AsMemory(), token);
        }
    }
}
