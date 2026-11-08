using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

public sealed partial class MailReadService
{
    public Task<MailboxWriteResult> MoveAsync(RequestIdentity identity, MessageReference message, string destination,
        CancellationToken cancellationToken)
    {
        InputValidator.Message(message);
        InputValidator.Folder(destination);
        var writes = RequireWrites(identity, message.AccountId);
        if (!writes.Move) throw new ConnectorException(ErrorCodes.AccessDenied);
        return ExecuteAsync(identity, message.AccountId, async (client, token) =>
        {
            var folder = await client.GetFolderAsync(destination, token);
            if (!writes.Trash && (folder.Attributes.HasFlag(FolderAttributes.Trash) || folder.FullName == writes.TrashFolder))
                throw new ConnectorException(ErrorCodes.AccessDenied);
            return await MoveMessageAsync(client, message, folder, token);
        }, cancellationToken, retry: false);
    }

    public Task<MailboxWriteResult> TrashAsync(RequestIdentity identity, MessageReference message, CancellationToken cancellationToken)
    {
        InputValidator.Message(message);
        var writes = RequireWrites(identity, message.AccountId);
        if (!writes.Trash) throw new ConnectorException(ErrorCodes.AccessDenied);
        return ExecuteAsync(identity, message.AccountId, async (client, token) =>
            await MoveMessageAsync(client, message, await ResolveWriteFolderAsync(client, writes.TrashFolder, SpecialFolder.Trash, token), token),
            cancellationToken, retry: false);
    }

    public async Task<MailboxWriteResult> CreateDraftAsync(RequestIdentity identity, string accountId, MailComposition message,
        CancellationToken cancellationToken)
    {
        var writes = RequireWrites(identity, accountId);
        if (!writes.CreateDraft) throw new ConnectorException(ErrorCodes.AccessDenied);
        using var draft = Compose(identity, accountId, message, requireRecipient: false);
        return await ExecuteAsync(identity, accountId, async (client, token) =>
        {
            var folder = await ResolveWriteFolderAsync(client, writes.DraftsFolder, SpecialFolder.Drafts, token);
            return await AppendDraftAsync(folder, accountId, draft, token);
        }, cancellationToken, retry: false);
    }

    public async Task<SendResult> SendAsync(RequestIdentity identity, string accountId, MailComposition message,
        Func<SendPreview, CancellationToken, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        var writes = RequireWrites(identity, accountId);
        if (!writes.Send || writes.Smtp is not { } smtp) throw new ConnectorException(ErrorCodes.AccessDenied);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Limits.OperationTimeoutSeconds));
        using var outgoing = Compose(identity, accountId, message, requireRecipient: true);
        if (writes.RequireSendConfirmation)
        {
            var preview = new SendPreview(((MailboxAddress)outgoing.From[0]).Address,
                outgoing.To.Mailboxes.Select(address => address.Address).ToArray(), message.Subject, message.Body);
            if (!await confirm(preview, deadline.Token)) throw new ConnectorException(ErrorCodes.ConfirmationDeclined);
        }

        if (Interlocked.Increment(ref admitted) > options.Limits.MaxConnections + options.Limits.MaxQueuedRequests)
        {
            Interlocked.Decrement(ref admitted);
            throw new ConnectorException(ErrorCodes.LimitExceeded);
        }
        var held = false;
        try
        {
            await concurrency.WaitAsync(deadline.Token);
            held = true;
            using var client = new SmtpClient { Timeout = options.Limits.OperationTimeoutSeconds * 1000 };
            return await ConnectAndSubmitAsync(client, smtp, outgoing, deadline.Token);
        }
        catch (MailKit.Security.AuthenticationException) { throw new ConnectorException(ErrorCodes.AuthenticationFailed); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ConnectorException(ErrorCodes.Timeout); }
        catch (Exception error) when (error is not (OperationCanceledException or ConnectorException))
        { throw new ConnectorException(ErrorCodes.ProviderUnavailable); }
        finally
        {
            if (held) concurrency.Release();
            Interlocked.Decrement(ref admitted);
        }
    }

    internal async Task<SendResult> ConnectAndSubmitAsync(SmtpClient client, SmtpSubmissionOptions smtp, MimeMessage message,
        CancellationToken token)
    {
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(options.Limits.ConnectTimeoutSeconds));
        using var socket = await NetworkPolicy.ConnectAsync(smtp.Host, smtp.Port, smtp.AllowedPrivateNetworks, connectDeadline.Token);
        await client.ConnectAsync(socket, smtp.Host, smtp.Port, smtp.Security == ImapSecurityMode.SslOnConnect
            ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, connectDeadline.Token);
        var credential = await credentials.GetCredentialAsync(smtp.CredentialReference, connectDeadline.Token);
        if (smtp.Authentication == ImapAuthenticationMode.OAuth2)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(smtp.Username, credential), connectDeadline.Token);
        else
        {
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            client.AuthenticationMechanisms.Remove("OAUTHBEARER");
            await client.AuthenticateAsync(smtp.Username, credential, connectDeadline.Token);
        }
        await WriteOnceAsync(ct => client.SendAsync(message, ct), token);
        return new SendResult(message.MessageId!);
    }

    private MailWriteOptions RequireWrites(RequestIdentity identity, string accountId)
    {
        var account = catalog.RequireAccount(identity, accountId);
        if (!identity.AllowWrites || account.Writes is not { } writes)
            throw new ConnectorException(ErrorCodes.AccessDenied);
        return writes;
    }

    private MimeMessage Compose(RequestIdentity identity, string accountId, MailComposition request, bool requireRecipient)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.To is not { Count: <= 50 } || (requireRecipient && request.To.Count == 0)
            || request.To.Any(address => !InputValidator.IsBareAddress(address))
            || request.Subject is null || request.Subject.Any(char.IsControl) || request.Body is null
            || request.Body.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ConnectorException(ErrorCodes.InvalidInput);
        if (Encoding.UTF8.GetByteCount(request.Subject) > options.Limits.MaxHeaderFieldBytes
            || Encoding.UTF8.GetByteCount(request.Body) > options.Limits.MaxTextBytes)
            throw new ConnectorException(ErrorCodes.LimitExceeded);
        var account = catalog.RequireAccount(identity, accountId);
        var sender = MailboxAddress.Parse(account.Writes!.FromAddress ?? account.Username);
        var message = new MimeMessage
        {
            MessageId = MimeUtils.GenerateMessageId(sender.Address[(sender.Address.LastIndexOf('@') + 1)..]),
            Subject = request.Subject,
            Body = new TextPart("plain") { Text = request.Body }
        };
        message.From.Add(sender);
        foreach (var address in request.To) message.To.Add(MailboxAddress.Parse(address));
        return message;
    }

    private static async Task<IMailFolder> ResolveWriteFolderAsync(ImapClient client, string? configured, SpecialFolder special,
        CancellationToken token)
    {
        IMailFolder? folder;
        try { folder = configured is null ? client.GetFolder(special) : await client.GetFolderAsync(configured, token); }
        catch (NotSupportedException) when (configured is null) { throw new ConnectorException(ErrorCodes.NotFound); }
        if (folder is null) throw new ConnectorException(ErrorCodes.NotFound);
        if (folder.Attributes.HasFlag(FolderAttributes.Inbox) || folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            throw new ConnectorException(ErrorCodes.InvalidConfiguration);
        return folder;
    }

    internal static async Task<MailboxWriteResult> MoveMessageAsync(ImapClient client, MessageReference message, IMailFolder destination,
        CancellationToken token)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Move)) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
        var source = await client.GetFolderAsync(message.Folder, token);
        if (ReferenceEquals(source, destination) || source.FullName == destination.FullName)
            throw new ConnectorException(ErrorCodes.InvalidInput);
        if (source.IsOpen && source.Access != FolderAccess.ReadWrite) await source.CloseAsync(false, token);
        if (!source.IsOpen && await source.OpenAsync(FolderAccess.ReadWrite, token) != FolderAccess.ReadWrite)
            throw new ConnectorException(ErrorCodes.AccessDenied);
        if (source.UidValidity != message.UidValidity) throw new ConnectorException(ErrorCodes.StaleReference);
        var uid = new UniqueId(message.UidValidity, message.Uid);
        var found = await source.FetchAsync([uid], MessageSummaryItems.UniqueId, token);
        if (!found.Any(summary => summary.UniqueId == uid)) throw new ConnectorException(ErrorCodes.NotFound);
        var moved = await WriteOnceAsync(ct => source.MoveToAsync(uid, destination, ct), token);
        return new MailboxWriteResult(destination.FullName, ToWriteReference(message.AccountId, destination, moved));
    }

    internal static async Task<MailboxWriteResult> AppendDraftAsync(IMailFolder folder, string accountId, MimeMessage draft,
        CancellationToken token)
    {
        var uid = await WriteOnceAsync(ct => folder.AppendAsync(draft, MessageFlags.Draft, ct), token);
        return new MailboxWriteResult(folder.FullName, ToWriteReference(accountId, folder, uid));
    }

    private static MessageReference? ToWriteReference(string accountId, IMailFolder folder, UniqueId? uid) =>
        uid is { IsValid: true, Validity: > 0 } value ? new(accountId, folder.FullName, value.Validity, value.Id) : null;

    private static async Task<T> WriteOnceAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await write(token); }
        catch (Exception error) when (error is IOException or ProtocolException or OperationCanceledException or ImapCommandException
            || error is ConnectorException { Code: ErrorCodes.LimitExceeded })
        { throw new ConnectorException(ErrorCodes.WriteOutcomeUnknown); }
    }
}
