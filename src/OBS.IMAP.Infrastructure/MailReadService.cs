using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using OBS.IMAP.Core;
using MessageSummary = OBS.IMAP.Core.MessageSummary;
using FolderInfo = OBS.IMAP.Core.FolderInfo;

namespace OBS.IMAP.Infrastructure;

public sealed partial class MailReadService : IMailReadService, IMailWriteService, IAsyncDisposable
{
    private readonly ConnectorOptions options;
    private readonly AccountCatalog catalog;
    private readonly ICredentialProvider credentials;
    private readonly CursorCodec cursors = new();
    private readonly SemaphoreSlim concurrency;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly object sync = new();
    private readonly Task eviction;
    private int admitted;

    private sealed class Session
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ImapClient? Client;
        public ProtocolBudget? Budget;
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
        public bool Busy;
        public void Close() { Client?.Dispose(); Client = null; Budget?.Dispose(); Budget = null; }
    }

    public MailReadService(ConnectorOptions options) : this(options, new CredentialProvider(options)) { }
    public MailReadService(ConnectorOptions options, ICredentialProvider credentials)
    {
        this.options = options;
        catalog = new AccountCatalog(options);
        this.credentials = credentials;
        concurrency = new SemaphoreSlim(options.Limits.MaxConnections);
        eviction = EvictIdleAsync();
    }

    public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(RequestIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(catalog.ListAccounts(identity));
    }

    public Task<Page<FolderInfo>> ListFoldersAsync(RequestIdentity identity, FolderListRequest request, CancellationToken cancellationToken = default)
    {
        InputValidator.Folders(request, options.Limits);
        return ExecuteAsync(identity, request.AccountId, async (client, token) =>
        {
            var parent = request.Parent ?? "";
            var offset = request.Continuation is null ? 0u : cursors.Decode(request.Continuation, identity.PrincipalId,
                request.AccountId, parent, "", "folders").Next;
            IEnumerable<IMailFolder> folders = parent.Length == 0
                ? await ListPersonalFoldersAsync(client, token)
                : await (await client.GetFolderAsync(parent, token)).GetSubfoldersAsync(false, token);
            var sorted = folders.OrderBy(folder => folder.FullName, StringComparer.Ordinal).ToArray();
            if (offset > sorted.Length) throw new ConnectorException(ErrorCodes.InvalidCursor);
            var page = sorted.Skip((int)offset).Take(request.PageSize).Select(ToFolderInfo).ToArray();
            var next = offset + (uint)page.Length;
            return new Page<FolderInfo>(page, next < sorted.Length
                ? cursors.Encode(new(identity.PrincipalId, request.AccountId, parent, "", 0, uint.MaxValue, next, Expiry(), "folders")) : null);
        }, cancellationToken);
    }

    internal static async Task<IReadOnlyList<IMailFolder>> ListPersonalFoldersAsync(ImapClient client, CancellationToken token)
    {
        var folders = new Dictionary<string, IMailFolder>(StringComparer.Ordinal);
        foreach (var folderNamespace in client.PersonalNamespaces)
        {
            foreach (var folder in await client.GetFoldersAsync(folderNamespace, false, token))
                folders.TryAdd(folder.FullName, folder);
        }
        return folders.Values.ToArray();
    }

    public Task<FolderInfo> GetFolderInfoAsync(RequestIdentity identity, string accountId, string folder, CancellationToken cancellationToken = default)
    {
        InputValidator.Folder(folder);
        return ExecuteAsync(identity, accountId, async (client, token) =>
        {
            var target = await client.GetFolderAsync(folder, token);
            return await ReadFolderInfoAsync(target, token);
        }, cancellationToken);
    }

    internal static async Task<FolderInfo> ReadFolderInfoAsync(IMailFolder folder, CancellationToken token)
    {
        // STATUS must not target the selected mailbox. The serialized account lease covers
        // closing and reading status, and read-only selection makes this close non-mutating.
        if (folder.IsOpen) await folder.CloseAsync(false, token);
        await folder.StatusAsync(StatusItems.Count | StatusItems.Unread | StatusItems.UidValidity, token);
        return ToFolderInfo(folder) with
        {
            MessageCount = folder.Count,
            UnreadCount = folder.Unread,
            UidValidity = folder.UidValidity
        };
    }

    public Task<Page<MessageSummary>> SearchMessagesAsync(RequestIdentity identity, SearchRequest request, CancellationToken cancellationToken = default)
    {
        InputValidator.Search(request, options.Limits);
        return ExecuteAsync(identity, request.AccountId, async (client, token) =>
        {
            var folder = await OpenAsync(client, request.Folder, token, refresh: request.Continuation is null);
            var filter = request.Filter ?? new SearchFilter();
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(filter)));
            var cursor = request.Continuation is null
                ? await StartUidScanAsync(identity, request.AccountId, request.Folder, hash, folder, token)
                : cursors.Decode(request.Continuation, identity.PrincipalId, request.AccountId, request.Folder, hash);
            if (cursor.Validity != folder.UidValidity) throw new ConnectorException(ErrorCodes.StaleReference);
            var query = BuildQuery(filter);
            var page = await UidPager.ReadAsync(cursor.Next, request.PageSize, options.Limits, async (low, high, ct) =>
            {
                var matches = await folder.SearchAsync(query.And(SearchQuery.Uids(new UniqueIdRange(new UniqueId(low), new UniqueId(high)))), ct);
                return matches.Select(uid => uid.Id).ToArray();
            }, token);
            var found = page.Uids.Select(uid => new UniqueId(uid)).ToArray();
            var summaries = found.Length == 0 ? [] : await folder.FetchAsync(found, MessageSummaryItems.UniqueId |
                MessageSummaryItems.Envelope | MessageSummaryItems.Size | MessageSummaryItems.Flags, token);
            if (summaries.Count > found.Length || summaries.Any(summary => !found.Contains(summary.UniqueId)))
                throw new ConnectorException(ErrorCodes.ProviderUnavailable);
            var items = summaries.OrderByDescending(summary => summary.UniqueId.Id)
                .Select(summary => ToSummary(request.AccountId, folder, summary)).ToArray();
            return new Page<MessageSummary>(items, page.Next > 0 ? cursors.Encode(cursor with { Next = page.Next }) : null);
        }, cancellationToken);
    }

    public Task<MessageHeaders> GetMessageHeadersAsync(RequestIdentity identity, MessageReference message, CancellationToken cancellationToken = default)
    {
        InputValidator.Message(message);
        return ExecuteAsync(identity, message.AccountId, async (client, token) =>
        {
            var folder = await OpenMessageAsync(client, message, token);
            var fields = new[] { HeaderId.From, HeaderId.To, HeaderId.Cc, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId, HeaderId.InReplyTo, HeaderId.References };
            var summaries = await folder.FetchAsync([new UniqueId(message.Uid)], MessageSummaryItems.UniqueId, fields, token);
            var summary = summaries.FirstOrDefault(summary => summary.UniqueId.Id == message.Uid) ?? throw new ConnectorException(ErrorCodes.NotFound);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var size = 0;
            foreach (var header in summary.Headers ?? [])
            {
                if (!fields.Contains(header.Id)) continue;
                var bytes = Encoding.UTF8.GetByteCount(header.Value);
                if (bytes > options.Limits.MaxHeaderFieldBytes || (size += bytes) > options.Limits.MaxHeaderBytes)
                    throw new ConnectorException(ErrorCodes.LimitExceeded);
                result[header.Field] = header.Value;
            }
            return new MessageHeaders(message, result);
        }, cancellationToken);
    }

    public Task<MessageStructure> GetMessageStructureAsync(RequestIdentity identity, MessageReference message, CancellationToken cancellationToken = default)
    {
        InputValidator.Message(message);
        return ExecuteAsync(identity, message.AccountId, async (client, token) =>
        {
            var folder = await OpenMessageAsync(client, message, token);
            var summary = await FetchStructureAsync(folder, message, token);
            return MapStructure(message, summary.Body);
        }, cancellationToken);
    }

    public Task<MessageContent> GetMessageAsync(RequestIdentity identity, MessageReference message, bool includeHtml = false, CancellationToken cancellationToken = default)
    {
        InputValidator.Message(message);
        return ExecuteAsync(identity, message.AccountId, async (client, token) =>
        {
            var folder = await OpenMessageAsync(client, message, token);
            var summary = await FetchStructureAsync(folder, message, token);
            var structure = MapStructure(message, summary.Body);
            string? plain = null, html = null;
            var truncated = false;
            var used = 0;
            foreach (var part in Flatten(summary.Body).OfType<BodyPartText>().Where(part => !IsAttachment(part)))
            {
                if ((!part.IsPlain || plain is not null) && (!includeHtml || !part.IsHtml || html is not null)) continue;
                await using var raw = await FetchPartStreamAsync(client, folder, message, part, token);
                using var decoded = new CappedMemoryStream(Math.Max(0, options.Limits.MaxTextBytes - used), true);
                using var contentDecoder = new MimeContent(raw, TransferEncoding(part.ContentTransferEncoding));
                await contentDecoder.DecodeToAsync(decoded, token);
                var data = decoded.ToArray();
                truncated |= decoded.Truncated;
                var encoding = MailTextEncoding.Get(part.ContentType.Charset);
                var content = encoding.GetString(data);
                var available = options.Limits.MaxTextBytes - used;
                if (Encoding.UTF8.GetByteCount(content) > available)
                {
                    var bounded = new StringBuilder();
                    foreach (var rune in content.EnumerateRunes())
                    {
                        if (rune.Utf8SequenceLength > available) break;
                        bounded.Append(rune.ToString());
                        available -= rune.Utf8SequenceLength;
                    }
                    content = bounded.ToString();
                    truncated = true;
                }
                used += Encoding.UTF8.GetByteCount(content);
                if (part.IsPlain) plain = content;
                else html = content;
            }
            return new MessageContent(message, plain, html, structure, truncated);
        }, cancellationToken);
    }

    public Task<AttachmentContent> GetAttachmentAsync(RequestIdentity identity, MessageReference message, string partId, CancellationToken cancellationToken = default)
    {
        InputValidator.Message(message);
        InputValidator.PartId(partId);
        return ExecuteAsync(identity, message.AccountId, async (client, token) =>
        {
            var folder = await OpenMessageAsync(client, message, token);
            var summary = await FetchStructureAsync(folder, message, token);
            _ = MapStructure(message, summary.Body);
            var part = Flatten(summary.Body).OfType<BodyPartBasic>().FirstOrDefault(part => part.PartSpecifier == partId);
            if (part is null || !IsAttachment(part)) throw new ConnectorException(ErrorCodes.NotFound);
            await using var raw = await FetchPartStreamAsync(client, folder, message, part, token);
            using var decoded = new CappedMemoryStream(options.Limits.MaxAttachmentBytes, false);
            using var contentDecoder = new MimeContent(raw, TransferEncoding(part.ContentTransferEncoding));
            await contentDecoder.DecodeToAsync(decoded, token);
            return new AttachmentContent(message, partId, part.FileName, part.ContentType.MimeType,
                Convert.ToBase64String(decoded.GetBuffer(), 0, (int)decoded.Length), decoded.Length);
        }, cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(RequestIdentity identity, string accountId, Func<ImapClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken, bool retry = true)
    {
        var account = catalog.RequireAccount(identity, accountId);
        if (stopping.IsCancellationRequested) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
        if (Interlocked.Increment(ref admitted) > options.Limits.MaxConnections + options.Limits.MaxQueuedRequests)
        { Interlocked.Decrement(ref admitted); throw new ConnectorException(ErrorCodes.LimitExceeded); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Limits.OperationTimeoutSeconds));
        var session = sessions.GetOrAdd(accountId, _ => new());
        var globalHeld = false;
        var accountHeld = false;
        try
        {
            await concurrency.WaitAsync(deadline.Token); globalHeld = true;
            await session.Gate.WaitAsync(deadline.Token); accountHeld = true;
            lock (sync)
            {
                session.Busy = true;
                if (session.Client is null && sessions.Values.Count(entry => entry.Client is not null || entry.Busy) > options.Limits.MaxConnections)
                    sessions.Values.Where(entry => !entry.Busy && entry.Client is not null).OrderBy(entry => entry.LastUsed).FirstOrDefault()?.Close();
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (session.Client is null || !session.Client.IsConnected)
                        await ConnectAsync(session, account, deadline.Token);
                    session.Budget!.Reset();
                    var result = await action(session.Client!, deadline.Token);
                    if (JsonSerializer.SerializeToUtf8Bytes(result).Length > options.Limits.MaxResponseBytes)
                        throw new ConnectorException(ErrorCodes.LimitExceeded);
                    return result;
                }
                catch (Exception exception) when (retry && attempt == 0 && exception is IOException or ImapProtocolException)
                { session.Close(); }
            }
        }
        catch (ConnectorException) { if (accountHeld) session.Close(); throw; }
        catch (OperationCanceledException)
        {
            if (accountHeld) session.Close();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new ConnectorException(ErrorCodes.Timeout);
        }
        catch (MailKit.Security.AuthenticationException) { if (accountHeld) session.Close(); throw new ConnectorException(ErrorCodes.AuthenticationFailed); }
        catch (FolderNotFoundException) { throw new ConnectorException(ErrorCodes.NotFound); }
        catch (MessageNotFoundException) { throw new ConnectorException(ErrorCodes.NotFound); }
        catch { if (accountHeld) session.Close(); throw new ConnectorException(ErrorCodes.ProviderUnavailable); }
        finally
        {
            if (accountHeld) { lock (sync) { session.Busy = false; session.LastUsed = DateTimeOffset.UtcNow; } session.Gate.Release(); }
            if (globalHeld) concurrency.Release();
            Interlocked.Decrement(ref admitted);
        }
    }

    private async Task ConnectAsync(Session session, AccountOptions account, CancellationToken token)
    {
        session.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Limits.ConnectTimeoutSeconds));
        var budget = new ProtocolBudget(options.Limits.MaxProtocolBytes, options.Limits.MaxPartWireBytes,
            options.Limits.MaxHeaderBytes, Math.Max(32, options.Limits.MaxMimeDepth * 3));
        var client = new ImapClient(budget) { Timeout = options.Limits.ConnectTimeoutSeconds * 1000 };
        session.Client = client;
        session.Budget = budget;
        var socket = await NetworkPolicy.ConnectAsync(account, deadline.Token);
        try
        {
            await client.ConnectAsync(socket, account.Host, account.Port, account.Security == ImapSecurityMode.SslOnConnect
                ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, deadline.Token);
            var credential = await credentials.GetCredentialAsync(account.CredentialReference, deadline.Token);
            if (account.Authentication == ImapAuthenticationMode.OAuth2)
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, credential), deadline.Token);
            else
            {
                // Prevent an accidental OAuth negotiation when password authentication was requested.
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
                await client.AuthenticateAsync(account.Username, credential, deadline.Token);
            }
        }
        catch { socket.Dispose(); session.Close(); throw; }
    }

    internal static async Task<IMailFolder> OpenAsync(ImapClient client, string name, CancellationToken token, bool refresh = false)
    {
        var folder = await client.GetFolderAsync(name, token);
        // A new scan needs a fresh EXAMINE response even when the session already selected this folder.
        if (folder.IsOpen && (refresh || folder.Access != FolderAccess.ReadOnly)) await folder.CloseAsync(false, token);
        if (!folder.IsOpen) await folder.OpenAsync(FolderAccess.ReadOnly, token);
        return folder;
    }

    private async Task<CursorState> StartUidScanAsync(RequestIdentity identity, string accountId, string name,
        string hash, IMailFolder folder, CancellationToken token, string kind = "search")
    {
        var upper = await ReadSearchUpperUidAsync(folder, token);
        return new CursorState(identity.PrincipalId, accountId, name, hash, folder.UidValidity, upper, upper, Expiry(), kind);
    }

    internal static async Task<uint> ReadSearchUpperUidAsync(IMailFolder folder, CancellationToken token)
    {
        if (folder.Count == 0) return 0;
        if (folder.UidNext is { } next && next.Id > 1) return next.Id - 1;

        // Some servers omit UIDNEXT. Read the UID of one message at the last known sequence number.
        var lastIndex = folder.Count - 1;
        var summaries = await folder.FetchAsync([lastIndex], MessageSummaryItems.UniqueId, token);
        if (summaries.Count == 1 && summaries[0].Index == lastIndex && summaries[0].UniqueId.IsValid)
            return summaries[0].UniqueId.Id;
        throw new ConnectorException(ErrorCodes.ProviderUnavailable);
    }
    private static async Task<IMailFolder> OpenMessageAsync(ImapClient client, MessageReference message, CancellationToken token)
    {
        var folder = await OpenAsync(client, message.Folder, token);
        if (folder.UidValidity != message.UidValidity) throw new ConnectorException(ErrorCodes.StaleReference);
        return folder;
    }
    private async Task<IMessageSummary> FetchStructureAsync(IMailFolder folder, MessageReference message, CancellationToken token)
    {
        var summary = (await folder.FetchAsync([new UniqueId(message.Uid)], MessageSummaryItems.UniqueId |
            MessageSummaryItems.Size | MessageSummaryItems.BodyStructure, token)).FirstOrDefault(summary => summary.UniqueId.Id == message.Uid)
                ?? throw new ConnectorException(ErrorCodes.NotFound);
        if (!summary.Size.HasValue || summary.Body is null) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
        if (summary.Size > options.Limits.MaxMessageBytes) throw new ConnectorException(ErrorCodes.LimitExceeded);
        return summary;
    }
    private async Task<Stream> FetchPartStreamAsync(ImapClient client, IMailFolder folder, MessageReference message, BodyPartBasic part, CancellationToken token)
    {
        if (part.Octets > options.Limits.MaxPartWireBytes) throw new ConnectorException(ErrorCodes.LimitExceeded);
        var budget = (ProtocolBudget)client.ProtocolLogger;
        budget.LiteralLimit = options.Limits.MaxPartWireBytes;
        // Raw BODY section contains transfer-encoded content, no MIME parser or recursive entity loading.
        var stream = await folder.GetStreamAsync(new UniqueId(message.Uid), part.PartSpecifier, 0, options.Limits.MaxPartWireBytes, token);
        budget.LiteralLimit = options.Limits.MaxHeaderBytes;
        return stream;
    }
    private static ContentEncoding TransferEncoding(string? encoding) => encoding?.ToLowerInvariant() switch
    {
        "base64" => ContentEncoding.Base64,
        "quoted-printable" => ContentEncoding.QuotedPrintable,
        "7bit" => ContentEncoding.SevenBit,
        "8bit" => ContentEncoding.EightBit,
        "binary" or null or "" => ContentEncoding.Binary,
        _ => throw new ConnectorException(ErrorCodes.ProviderUnavailable)
    };
    private MessageStructure MapStructure(MessageReference message, BodyPart? root)
    {
        var count = 0;
        MimePartInfo Map(BodyPart part, int depth)
        {
            if (++count > options.Limits.MaxMimeParts || depth > options.Limits.MaxMimeDepth) throw new ConnectorException(ErrorCodes.LimitExceeded);
            var basic = part as BodyPartBasic;
            var children = part is BodyPartMultipart multipart ? multipart.BodyParts.Select(child => Map(child, depth + 1)).ToArray()
                : part is BodyPartMessage nested && nested.Body is not null ? new[] { Map(nested.Body, depth + 1) } : [];
            return new MimePartInfo(part.PartSpecifier, part.ContentType.MimeType, basic?.FileName, basic?.Octets,
                basic is not null && IsAttachment(basic), children);
        }
        return new MessageStructure(message, root is null ? [] : [Map(root, 1)]);
    }
    private static bool IsAttachment(BodyPartBasic part) => part.IsAttachment || !string.IsNullOrEmpty(part.FileName);

    private static IEnumerable<BodyPart> Flatten(BodyPart? root)
    {
        if (root is null) yield break;
        yield return root;
        // Do not automatically retrieve content inside attached message/rfc822 parts.
        if (root is BodyPartMultipart multipart)
            foreach (var child in multipart.BodyParts)
                foreach (var part in Flatten(child)) yield return part;
    }
    private static FolderInfo ToFolderInfo(IMailFolder folder) => new(folder.FullName, folder.DirectorySeparator,
        folder.Attributes.ToString().Split(", "), folder.IsOpen ? folder.Count : null, null,
        folder.IsOpen ? folder.UidValidity : null);
    private static MessageSummary ToSummary(string account, IMailFolder folder, IMessageSummary summary) => new(
        new(account, folder.FullName, folder.UidValidity, summary.UniqueId.Id), summary.Envelope?.Subject,
        summary.Envelope?.From?.ToString(), summary.Envelope?.To?.ToString(), summary.Envelope?.Date,
        summary.Size ?? 0, summary.Flags?.ToString().Split(", ") ?? []);
    private static SearchQuery BuildQuery(SearchFilter filter)
    {
        var query = SearchQuery.All;
        if (filter.Sender is { } sender) query = query.And(SearchQuery.FromContains(sender));
        if (filter.Recipient is { } recipient) query = query.And(SearchQuery.ToContains(recipient).Or(SearchQuery.CcContains(recipient)));
        if (filter.Subject is { } subject) query = query.And(SearchQuery.SubjectContains(subject));
        if (filter.Text is { } text) query = query.And(SearchQuery.MessageContains(text));
        if (filter.ReceivedAfter is { } after) query = query.And(SearchQuery.DeliveredAfter(after.ToDateTime(TimeOnly.MinValue)));
        if (filter.ReceivedBefore is { } before) query = query.And(SearchQuery.DeliveredBefore(before.ToDateTime(TimeOnly.MinValue)));
        if (filter.IsRead is { } read) query = query.And(read ? SearchQuery.Seen : SearchQuery.NotSeen);
        if (filter.IsFlagged is { } flagged) query = query.And(flagged ? SearchQuery.Flagged : SearchQuery.NotFlagged);
        return query;
    }
    private long Expiry() => DateTimeOffset.UtcNow.AddSeconds(options.Limits.CursorLifetimeSeconds).ToUnixTimeSeconds();
    private async Task EvictIdleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, Math.Min(30, options.Limits.IdleTimeoutSeconds))));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token))
                lock (sync)
                    foreach (var session in sessions.Values.Where(entry => !entry.Busy && entry.LastUsed.AddSeconds(options.Limits.IdleTimeoutSeconds) < DateTimeOffset.UtcNow)) session.Close();
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await eviction;
        foreach (var session in sessions.Values)
        {
            await session.Gate.WaitAsync();
            try { session.Close(); } finally { session.Gate.Release(); }
        }
        cursors.Dispose();
        stopping.Dispose();
    }
}

internal sealed class CappedMemoryStream(int limit, bool truncate) : MemoryStream
{
    public bool Truncated { get; private set; }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _ = buffer.AsSpan(offset, count);
        base.Write(buffer, offset, AllowedLength(count));
    }
    public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var count = AllowedLength(buffer.Length);
        if (count == 0) return;
        var copy = buffer[..count].ToArray();
        // MemoryStream's span overload dispatches through virtual array Write for derived streams.
        base.Write(copy, 0, copy.Length);
    }
    private int AllowedLength(int requested)
    {
        var available = Math.Max(0, limit - (int)Length);
        if (requested > available)
        {
            if (!truncate) throw new ConnectorException(ErrorCodes.LimitExceeded);
            Truncated = true;
        }
        return Math.Min(requested, available);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
}
