using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using ModelContextProtocol.Client;
using OBS.IMAP.Core;
using OBS.IMAP.Infrastructure;
using Xunit;

namespace OBS.IMAP.Integration.Tests;

public sealed class ImapFactAttribute : FactAttribute
{
    public ImapFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OBS_IMAP_INTEGRATION") != "1")
            Skip = "Requires the disposable Dovecot fixture, trusted test CA and OBS_IMAP_INTEGRATION=1; see docker/integration.";
    }
}

[CollectionDefinition("Dovecot", DisableParallelization = true)]
public sealed class DovecotCollection;

[Collection("Dovecot")]
public sealed class DovecotTests
{
    private const string SecretVariable = "OBS_IMAP_INTEGRATION_SYNTHETIC_PASSWORD";
    private static readonly RequestIdentity Identity = new("integration", "fixture", ["fixture"]);

    [ImapFact]
    public async Task Production_stdio_pipeline_reads_real_mail_and_keeps_diagnostics_private()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var before = await ReadFlagsAsync(deadline.Token);
        var path = Path.Combine(Path.GetTempPath(), $"obs-imap-dovecot-{Guid.NewGuid():N}.json");
        var diagnostics = new ConcurrentQueue<string>();
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Connector = Options(),
                Local = new { AllowedAccountIds = new[] { "fixture" } }
            }), deadline.Token);
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [typeof(OBS.IMAP.Host.Program).Assembly.Location, "--config", path, "--transport", "stdio"],
                StandardErrorLines = diagnostics.Enqueue,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });
            await using (var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token))
            {
                Assert.Equal(8, (await client.ListToolsAsync(cancellationToken: deadline.Token)).Count);
                var info = await client.CallToolAsync("get_folder_info", new Dictionary<string, object?>
                {
                    ["accountId"] = "fixture", ["folder"] = "INBOX"
                }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, info.IsError);
                Assert.True(info.StructuredContent!.Value.GetProperty("data").GetProperty("uidValidity").GetUInt32() > 0);
                var search = await client.CallToolAsync("search_messages", new Dictionary<string, object?>
                {
                    ["request"] = new { accountId = "fixture", folder = "INBOX", filter = new { subject = "fixture one" }, pageSize = 1 }
                }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, search.IsError);
                var message = Assert.Single(search.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray());
                var result = await client.CallToolAsync("get_message", new Dictionary<string, object?>
                {
                    ["message"] = message.GetProperty("reference").Clone(), ["includeHtml"] = false
                }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, result.IsError);
                Assert.Contains("Plain fixture message one.", result.StructuredContent!.Value.GetProperty("data").GetProperty("plainText").GetString());
                var part = result.StructuredContent.Value.GetProperty("data").GetProperty("structure").GetProperty("parts")[0]
                    .GetProperty("children")[0];
                Assert.Equal("inline-note.txt", part.GetProperty("fileName").GetString());
                Assert.True(part.GetProperty("isAttachment").GetBoolean());
                var attachment = await client.CallToolAsync("get_attachment", new Dictionary<string, object?>
                {
                    ["message"] = message.GetProperty("reference").Clone(), ["partId"] = part.GetProperty("partId").GetString()
                }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, attachment.IsError);
                var bytes = Convert.FromBase64String(attachment.StructuredContent!.Value.GetProperty("data").GetProperty("base64").GetString()!);
                Assert.Equal("Synthetic inline attachment.", Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n'));
            }
            var stderr = string.Join('\n', diagnostics);
            Assert.DoesNotContain("fixture-password", stderr);
            Assert.DoesNotContain("Plain fixture message one.", stderr);
            Assert.DoesNotContain("Synthetic inline attachment.", stderr);
            var after = await ReadFlagsAsync(deadline.Token);
            Assert.Equal(before.OrderBy(entry => entry.Key), after.OrderBy(entry => entry.Key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretVariable, null);
            File.Delete(path);
        }
    }

    [ImapFact]
    public async Task All_eight_read_operations_work_without_changing_flags()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var before = await ReadFlagsAsync(token);
        Assert.True(before.Count >= 3, "The Dovecot fixture must be seeded with at least three messages.");
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            await using var service = new MailReadService(Options());
            Assert.Equal("fixture", Assert.Single(await service.ListAccountsAsync(Identity, token)).Id);
            var folders = await service.ListFoldersAsync(Identity, new FolderListRequest("fixture"), token);
            Assert.Contains(folders.Items, folder => folder.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(folders.Items, folder => folder.Name == "Archive");
            var info = await service.GetFolderInfoAsync(Identity, "fixture", "INBOX", token);
            Assert.True(info.UidValidity > 0);
            Assert.Equal(before.Values.Count(flags => ((flags ?? MessageFlags.None) & MessageFlags.Seen) == 0), info.UnreadCount);
            var found = await service.SearchMessagesAsync(Identity, new SearchRequest("fixture", "INBOX"), token);
            Assert.True(found.Items.Count >= 3);
            var foundAttachment = false;
            var foundText = false;
            var foundUnicodeAndHtml = false;
            foreach (var message in found.Items)
            {
                Assert.Equal(info.UidValidity, message.Reference.UidValidity);
                var headers = await service.GetMessageHeadersAsync(Identity, message.Reference, token);
                Assert.NotEmpty(headers.Headers);
                var content = await service.GetMessageAsync(Identity, message.Reference, true, token);
                foundText |= !string.IsNullOrWhiteSpace(content.PlainText) || !string.IsNullOrWhiteSpace(content.Html);
                foundUnicodeAndHtml |= content.PlainText?.Contains("æøå", StringComparison.Ordinal) == true && !string.IsNullOrEmpty(content.Html);
                Assert.Contains("untrusted", content.ContentWarning, StringComparison.OrdinalIgnoreCase);
                var structure = await service.GetMessageStructureAsync(Identity, message.Reference, token);
                foreach (var part in Flatten(structure.Parts).Where(part => part.FileName == "sample.txt"))
                {
                    var attachment = await service.GetAttachmentAsync(Identity, message.Reference, part.PartId, token);
                    var bytes = Convert.FromBase64String(attachment.Base64);
                    Assert.Equal(bytes.LongLength, attachment.ByteCount);
                    Assert.Equal("attachment-data", Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n'));
                    foundAttachment = true;
                }
            }
            Assert.True(foundText, "The fixture must contain a readable text or HTML body.");
            Assert.True(foundUnicodeAndHtml, "The fixture must contain decoded Unicode plain text and HTML alternatives.");
            Assert.True(foundAttachment, "The fixture must contain sample.txt with attachment-data.");
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
        var after = await ReadFlagsAsync(token);
        Assert.Equal(before.OrderBy(entry => entry.Key), after.OrderBy(entry => entry.Key));
    }

    [ImapFact]
    public async Task Search_pages_resume_without_duplicate_uids_and_apply_server_filter()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            await using var service = new MailReadService(Options());
            var seen = new HashSet<uint>();
            string? continuation = null;
            var pages = 0;
            do
            {
                var page = await service.SearchMessagesAsync(Identity, new SearchRequest("fixture", "INBOX", PageSize: 1, Continuation: continuation), deadline.Token);
                Assert.True(page.Items.Count <= 1);
                foreach (var item in page.Items) Assert.True(seen.Add(item.Reference.Uid), "Continuation repeated a UID.");
                continuation = page.Continuation;
                Assert.True(++pages <= 20, "Fixture pagination did not terminate.");
            } while (continuation is not null);
            Assert.True(seen.Count >= 3);
            var noMatches = await service.SearchMessagesAsync(Identity, new SearchRequest("fixture", "INBOX",
                new SearchFilter { Subject = "OBS-IMAP-no-such-subject-42bc4cce" }), deadline.Token);
            Assert.Empty(noMatches.Items);
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
    }

    [ImapFact]
    public async Task Stale_references_and_cross_principal_continuations_are_rejected()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            await using var service = new MailReadService(Options());
            var first = await service.SearchMessagesAsync(Identity, new SearchRequest("fixture", "INBOX", PageSize: 1), deadline.Token);
            var message = Assert.Single(first.Items).Reference;
            Assert.NotNull(first.Continuation);
            var staleValidity = message.UidValidity == uint.MaxValue ? 1 : message.UidValidity + 1;
            var stale = await Assert.ThrowsAsync<ConnectorException>(() => service.GetMessageHeadersAsync(Identity,
                message with { UidValidity = staleValidity }, deadline.Token));
            Assert.Equal(ErrorCodes.StaleReference, stale.Code);

            // Both principals may read this account, but one cannot replay another's search cursor.
            var otherPrincipal = new RequestIdentity("integration", "different-subject", ["fixture"]);
            var replay = await Assert.ThrowsAsync<ConnectorException>(() => service.SearchMessagesAsync(otherPrincipal,
                new SearchRequest("fixture", "INBOX", PageSize: 1, Continuation: first.Continuation), deadline.Token));
            Assert.Equal(ErrorCodes.InvalidCursor, replay.Code);

            var unauthorized = new RequestIdentity("integration", "no-accounts", []);
            var denied = await Assert.ThrowsAsync<ConnectorException>(() => service.GetMessageHeadersAsync(unauthorized, message, deadline.Token));
            Assert.Equal(ErrorCodes.AccessDenied, denied.Code);

            // Invalid attempts must not invalidate a valid principal's authenticated cursor.
            var next = await service.SearchMessagesAsync(Identity,
                new SearchRequest("fixture", "INBOX", PageSize: 1, Continuation: first.Continuation), deadline.Token);
            Assert.NotEqual(message.Uid, Assert.Single(next.Items).Reference.Uid);
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
    }

    [ImapFact]
    public async Task Hundred_thousand_message_folder_search_stays_bounded_and_resumes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            var options = Options() with
            {
                Limits = new ConnectorLimits { UidIntervalWidth = 1000, MaxSearchIntervals = 2, OperationTimeoutSeconds = 60 }
            };
            await using var service = new MailReadService(options);
            // Warm up Dovecot's folder/index discovery before measuring request allocations.
            var info = await service.GetFolderInfoAsync(Identity, "fixture", "Large", deadline.Token);
            Assert.Equal(100_000, info.MessageCount);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var first = await service.SearchMessagesAsync(Identity, new SearchRequest("fixture", "Large", PageSize: 5), deadline.Token);
            Assert.Equal(5, first.Items.Count);
            Assert.NotNull(first.Continuation);
            var second = await service.SearchMessagesAsync(Identity,
                new SearchRequest("fixture", "Large", PageSize: 5, Continuation: first.Continuation), deadline.Token);
            Assert.Equal(5, second.Items.Count);
            var uids = first.Items.Concat(second.Items).Select(item => item.Reference.Uid).ToArray();
            Assert.Equal(10, uids.Distinct().Count());
            Assert.Equal(uids.OrderByDescending(uid => uid), uids);

            // Only two UID intervals are examined, so no-match is not end-of-folder.
            var empty = await service.SearchMessagesAsync(Identity, new SearchRequest("fixture", "Large",
                new SearchFilter { Subject = "OBS-IMAP-no-such-subject-42bc4cce" }, PageSize: 5), deadline.Token);
            Assert.Empty(empty.Items);
            Assert.NotNull(empty.Continuation);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.True(allocatedBytes < 64L * 1024 * 1024,
                $"Three bounded summary searches allocated {allocatedBytes:N0} bytes; expected less than 64 MiB. No message bodies are requested.");
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
    }

    [ImapFact]
    public async Task Incorrect_password_returns_safe_authentication_error()
    {
        Environment.SetEnvironmentVariable(SecretVariable, "incorrect-synthetic-password");
        try
        {
            await using var service = new MailReadService(Options());
            var error = await Assert.ThrowsAsync<ConnectorException>(() => service.GetFolderInfoAsync(Identity, "fixture", "INBOX"));
            Assert.Equal(ErrorCodes.AuthenticationFailed, error.Code);
            Assert.DoesNotContain("incorrect-synthetic-password", error.ToString());
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
    }

    [ImapFact]
    public async Task Certificate_hostname_mismatch_is_rejected()
    {
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            var options = Options();
            await using var service = new MailReadService(options with { Accounts = [options.Accounts[0] with { Host = "127.0.0.1" }] });
            var error = await Assert.ThrowsAsync<ConnectorException>(() => service.GetFolderInfoAsync(Identity, "fixture", "INBOX"));
            Assert.Equal(ErrorCodes.ProviderUnavailable, error.Code);
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
    }

    [ImapFact]
    public async Task Required_starttls_authenticates_and_reads_folder()
    {
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-password");
        try
        {
            await using var service = new MailReadService(Options(startTls: true));
            var info = await service.GetFolderInfoAsync(Identity, "fixture", "INBOX");
            Assert.True(info.MessageCount >= 3);
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, null); }
    }

    private static ConnectorOptions Options(bool startTls = false) => new()
    {
        Accounts = [new AccountOptions
        {
            Id = "fixture", Host = "localhost", Port = startTls ? 1143 : 1993,
            Security = startTls ? ImapSecurityMode.StartTls : ImapSecurityMode.SslOnConnect,
            Username = "fixture", CredentialReference = "env:" + SecretVariable,
            AllowedPrivateNetworks = ["127.0.0.0/8", "::1/128"]
        }]
    };

    private static async Task<Dictionary<uint, MessageFlags?>> ReadFlagsAsync(CancellationToken token)
    {
        using var client = new ImapClient();
        await client.ConnectAsync("localhost", 1993, SecureSocketOptions.SslOnConnect, token);
        await client.AuthenticateAsync("fixture", "fixture-password", token);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, token);
        var uids = await client.Inbox.SearchAsync(SearchQuery.All, token);
        var summaries = await client.Inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, token);
        var flags = summaries.ToDictionary(summary => summary.UniqueId.Id, summary => summary.Flags);
        await client.DisconnectAsync(true, token);
        return flags;
    }

    private static IEnumerable<MimePartInfo> Flatten(IEnumerable<MimePartInfo> parts)
    {
        foreach (var part in parts)
        {
            yield return part;
            foreach (var child in Flatten(part.Children)) yield return child;
        }
    }
}
