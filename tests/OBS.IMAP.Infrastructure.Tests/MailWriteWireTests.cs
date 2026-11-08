using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class MailWriteWireTests
{
    [Theory]
    [InlineData(42, true)]
    [InlineData(41, true)]
    [InlineData(42, false)]
    public async Task Move_is_uid_scoped_and_draft_append_sets_draft_flag(uint validity, bool supportsMove)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var commands = new List<string>();
        var correctDraft = false;
        var fixture = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync($"* PREAUTH [CAPABILITY IMAP4rev1 UIDPLUS LITERAL+{(supportsMove ? " MOVE" : "")}] synthetic");
            while (await reader.ReadLineAsync(token) is { } line)
            {
                var tagEnd = line.IndexOf(' ');
                var tag = line[..tagEnd];
                var command = line[(tagEnd + 1)..];
                commands.Add(command);
                var response = command.Split(' ')[0] switch
                {
                    "LIST" => "* LIST () \"/\" INBOX\r\n* LIST (\\Trash) \"/\" Trash\r\n* LIST (\\Drafts) \"/\" Drafts\r\n",
                    "SELECT" => "* 1 EXISTS\r\n* FLAGS (\\Seen \\Draft)\r\n* OK [UIDVALIDITY 42] valid\r\n",
                    "UID" when command.StartsWith("UID FETCH", StringComparison.Ordinal) => "* 1 FETCH (UID 7)\r\n",
                    "UID" when command.StartsWith("UID MOVE", StringComparison.Ordinal) => "* OK [COPYUID 43 7 9] copied\r\n* 1 EXPUNGE\r\n",
                    "LOGOUT" => "* BYE complete\r\n",
                    _ => ""
                };
                var code = command.StartsWith("SELECT ", StringComparison.Ordinal) ? "[READ-WRITE] " : "";
                if (command.StartsWith("APPEND ", StringComparison.Ordinal))
                {
                    if (!command.EndsWith("+}", StringComparison.Ordinal)) await writer.WriteLineAsync("+ ready");
                    var length = int.Parse(command[(command.LastIndexOf('{') + 1)..].TrimEnd('}', '+'),
                        System.Globalization.CultureInfo.InvariantCulture);
                    var literal = new char[length];
                    Assert.Equal(length, await reader.ReadBlockAsync(literal, token));
                    Assert.Empty((await reader.ReadLineAsync(token))!);
                    correctDraft = new string(literal).Contains("Synthetic body.", StringComparison.Ordinal);
                    code = "[APPENDUID 44 10] ";
                }
                await writer.WriteAsync($"{response}{tag} OK {code}complete\r\n");
                if (command == "LOGOUT") break;
            }
        }, token);

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, token);
        var trash = await client.GetFolderAsync("Trash", token);
        var message = new MessageReference("fixture", "INBOX", validity, 7);
        if (validity == 42 && supportsMove)
        {
            var moved = await MailReadService.MoveMessageAsync(client, message, trash, token);
            Assert.Equal(new MessageReference("fixture", "Trash", 43, 9), moved.Reference);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConnectorException>(() => MailReadService.MoveMessageAsync(client, message, trash, token));
            Assert.Equal(supportsMove ? ErrorCodes.StaleReference : ErrorCodes.ProviderUnavailable, error.Code);
        }

        using var draft = SyntheticMessage();
        var drafts = await client.GetFolderAsync("Drafts", token);
        var saved = await MailReadService.AppendDraftAsync(drafts, "fixture", draft, token);
        Assert.Equal(new MessageReference("fixture", "Drafts", 44, 10), saved.Reference);
        await client.DisconnectAsync(true, token);
        await fixture;

        Assert.True(correctDraft);
        Assert.Contains(commands, command => command.StartsWith("APPEND Drafts (\\Draft) ", StringComparison.Ordinal));
        Assert.Equal(validity == 42 && supportsMove ? 1 : 0, commands.Count(command => command == "UID MOVE 7 Trash"));
        Assert.DoesNotContain(commands, command => command.StartsWith("EXPUNGE", StringComparison.Ordinal)
            || command.StartsWith("UID STORE", StringComparison.Ordinal) || command.StartsWith("UID COPY", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Smtp_authenticates_over_tls_and_submits_once_even_if_acknowledgment_is_lost(bool acknowledge)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // Schannel requires a key backed by a temporary user key container.
        var pfx = generated.Export(X509ContentType.Pfx);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var fixture = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(token);
            await using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, _) => certificate
            }, token);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 synthetic SMTP");
            Assert.StartsWith("EHLO ", (await reader.ReadLineAsync(token))!);
            await writer.WriteAsync("250-synthetic\r\n250 AUTH PLAIN\r\n");
            var auth = await reader.ReadLineAsync(token);
            Assert.True(auth == "AUTH PLAIN " + Convert.ToBase64String(Encoding.ASCII.GetBytes("\0fixture\0synthetic-password")));
            await writer.WriteLineAsync("235 authenticated");
            Assert.Equal("MAIL FROM:<sender@example.test>", await reader.ReadLineAsync(token));
            await writer.WriteLineAsync("250 sender accepted");
            Assert.Equal("RCPT TO:<recipient@example.test>", await reader.ReadLineAsync(token));
            await writer.WriteLineAsync("250 recipient accepted");
            Assert.Equal("DATA", await reader.ReadLineAsync(token));
            await writer.WriteLineAsync("354 enter message");
            var sawBody = false;
            while (await reader.ReadLineAsync(token) is { } line && line != ".")
                sawBody |= line == "Synthetic body.";
            Assert.True(sawBody);
            if (acknowledge) await writer.WriteLineAsync("250 queued");
            else listener.Stop();
        }, token);

        var smtp = new SmtpSubmissionOptions
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Username = "fixture", CredentialReference = "env:SYNTHETIC", AllowedPrivateNetworks = ["127.0.0.1/32"]
        };
        await using var service = new MailReadService(new ConnectorOptions(), new FixtureCredential());
        // Trust only this ephemeral certificate, and only on this fixture client.
        using var client = new SmtpClient
        {
            ServerCertificateValidationCallback = (_, candidate, _, _) => candidate?.GetCertHashString() == certificate.Thumbprint
        };
        using var message = SyntheticMessage();
        try
        {
            if (acknowledge)
                Assert.Equal(message.MessageId, (await service.ConnectAndSubmitAsync(client, smtp, message, token)).MessageId);
            else
                Assert.Equal(ErrorCodes.WriteOutcomeUnknown, (await Assert.ThrowsAsync<ConnectorException>(
                    () => service.ConnectAndSubmitAsync(client, smtp, message, token))).Code);
        }
        finally { await fixture; }
    }

    private static MimeMessage SyntheticMessage()
    {
        var message = new MimeMessage { MessageId = "synthetic@example.test", Subject = "Synthetic draft", Body = new TextPart("plain") { Text = "Synthetic body." } };
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("recipient@example.test"));
        return message;
    }

    private sealed class FixtureCredential : ICredentialProvider
    {
        public ValueTask<string> GetCredentialAsync(string credentialReference, CancellationToken cancellationToken) =>
            ValueTask.FromResult("synthetic-password");
    }
}
