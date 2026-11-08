using System.Security.Cryptography;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

public sealed partial class MailReadService
{
    // Only the local operator CLI calls this operation. It is deliberately absent from MCP registration.
    public Task<InboxCleanupPage> CleanupInboxAsync(RequestIdentity identity, string accountId, bool apply = false,
        string? continuation = null, CancellationToken cancellationToken = default)
    {
        var account = catalog.RequireAccount(identity, accountId);
        if (!options.EnableInboxCleanup || account.InboxCleanup is not { Enabled: true } policy)
            throw new ConnectorException(ErrorCodes.AccessDenied);
        InputValidator.Continuation(continuation);
        return ExecuteAsync(identity, accountId, async (client, token) =>
        {
            // Never use MailKit's COPY/STORE/EXPUNGE fallback on servers without native MOVE.
            RequireNativeCleanupMove(client.Capabilities);
            var trash = await client.GetFolderAsync(policy.TrashFolder, token);
            ValidateTrash(trash);
            var folder = await OpenAsync(client, "INBOX", token, refresh: continuation is null);
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { policy, apply })));
            var cursor = continuation is null
                ? await StartUidScanAsync(identity, accountId, "INBOX", hash, folder, token, "cleanup")
                : cursors.Decode(continuation, identity.PrincipalId, accountId, "INBOX", hash, "cleanup");
            if (cursor.Validity != folder.UidValidity) throw new ConnectorException(ErrorCodes.StaleReference);
            var page = await UidPager.ReadAsync(cursor.Next, Math.Min(100, options.Limits.MaxSearchPageSize), options.Limits,
                async (low, high, ct) => (await folder.SearchAsync(SearchQuery.FromContains(policy.Sender)
                    .And(SearchQuery.Uids(new UniqueIdRange(new UniqueId(low), new UniqueId(high)))), ct))
                    .Select(uid => uid.Id).ToArray(), token);
            var candidates = page.Uids.Select(uid => new UniqueId(uid)).ToArray();
            var summaries = candidates.Length == 0 ? [] : await folder.FetchAsync(candidates,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, token);
            if (summaries.Count > candidates.Length || summaries.Any(s => !candidates.Contains(s.UniqueId))
                || summaries.Select(s => s.UniqueId).Distinct().Count() != summaries.Count)
                throw new ConnectorException(ErrorCodes.ProviderUnavailable);
            // IMAP FROM search is a substring match. Only one exact mailbox in From is eligible.
            var matched = summaries.Where(s => IsExactCleanupSender(s.Envelope?.From, policy.Sender))
                .Select(s => s.UniqueId).ToArray();
            if (apply && matched.Length > 0)
            {
                await folder.CloseAsync(false, token);
                await folder.OpenAsync(FolderAccess.ReadWrite, token);
                if (folder.UidValidity != cursor.Validity) throw new ConnectorException(ErrorCodes.StaleReference);
                await MoveCleanupMatchesAsync(folder, trash, matched, cursor.Validity, token);
                await folder.CloseAsync(false, token);
            }
            return new InboxCleanupPage(matched.Length, apply ? matched.Length : 0,
                page.Next > 0 ? cursors.Encode(cursor with { Next = page.Next }) : null);
        }, cancellationToken, retry: false);
    }

    internal static bool IsExactCleanupSender(InternetAddressList? from, string expected) =>
        from is { Count: 1 } && from[0] is MailboxAddress mailbox &&
        string.Equals(mailbox.Address, expected, StringComparison.OrdinalIgnoreCase);

    internal static void RequireNativeCleanupMove(ImapCapabilities capabilities)
    {
        if (!capabilities.HasFlag(ImapCapabilities.Move)) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
    }

    internal static void ValidateTrash(IMailFolder trash)
    {
        if (!trash.Attributes.HasFlag(FolderAttributes.Trash) || trash.Attributes.HasFlag(FolderAttributes.Inbox)
            || trash.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            throw new ConnectorException(ErrorCodes.InvalidConfiguration);
    }

    internal static async Task MoveCleanupMatchesAsync(IMailFolder inbox, IMailFolder trash,
        IList<UniqueId> uids, uint expectedValidity, CancellationToken token)
    {
        ValidateTrash(trash);
        if (inbox.UidValidity != expectedValidity) throw new ConnectorException(ErrorCodes.StaleReference);
        await inbox.MoveToAsync(uids, trash, token);
    }
}
