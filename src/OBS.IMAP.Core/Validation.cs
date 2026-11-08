using System.Net;
using System.Text.RegularExpressions;

namespace OBS.IMAP.Core;

public static partial class InputValidator
{
    public static bool IsBareAddress(string? value) =>
        value is { Length: > 0 and <= 254 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && System.Net.Mail.MailAddress.TryCreate(value, out var address) && address.Address == value;

    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("\\A[1-9][0-9]*(\\.[1-9][0-9]*)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    public static bool IsAccountId(string? value) => value is not null && IdentifierPattern().IsMatch(value);

    public static void AccountId(string? value)
    {
        if (!IsAccountId(value)) throw new ConnectorException(ErrorCodes.InvalidInput);
    }

    public static void Folder(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(char.IsControl))
            throw new ConnectorException(ErrorCodes.InvalidInput);
    }

    public static void Message(MessageReference? value)
    {
        if (value is null || value.Uid == 0 || value.UidValidity == 0)
            throw new ConnectorException(ErrorCodes.InvalidInput);
        AccountId(value.AccountId);
        Folder(value.Folder);
    }

    public static void PartId(string? value)
    {
        if (value is null || value.Length > 128 || !PartPattern().IsMatch(value))
            throw new ConnectorException(ErrorCodes.InvalidInput);
    }

    public static void Search(SearchRequest request, ConnectorLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        AccountId(request.AccountId);
        Folder(request.Folder);
        PageSize(request.PageSize, limits.MaxSearchPageSize);
        Continuation(request.Continuation);
        if (request.Filter is not { } filter) return;
        foreach (var text in new[] { filter.Sender, filter.Recipient, filter.Subject, filter.Text })
            if (text is not null && (text.Length > 1024 || text.Any(char.IsControl)))
                throw new ConnectorException(ErrorCodes.InvalidInput);
        if (filter.ReceivedAfter is { } after && filter.ReceivedBefore is { } before && after >= before)
            throw new ConnectorException(ErrorCodes.InvalidInput);
    }

    public static void Folders(FolderListRequest request, ConnectorLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        AccountId(request.AccountId);
        if (request.Parent is not null) Folder(request.Parent);
        PageSize(request.PageSize, limits.MaxFolderPageSize);
        Continuation(request.Continuation);
    }

    public static void PageSize(int value, int maximum)
    {
        if (value < 1 || value > maximum) throw new ConnectorException(ErrorCodes.InvalidInput);
    }

    public static void Continuation(string? value)
    {
        if (value is not null && (value.Length is < 1 or > 8192 || value.Any(char.IsControl)))
            throw new ConnectorException(ErrorCodes.InvalidCursor);
    }
}

public static class ConfigurationValidator
{
    public static void Validate(ConnectorOptions options)
    {
        if (GetErrors(options).Count != 0) throw new ConnectorException(ErrorCodes.InvalidConfiguration);
    }

    // Paths contain field names and indices, never operator values or credentials.
    public static IReadOnlyList<string> GetErrors(ConnectorOptions? options)
    {
        if (options is null) return ["configuration"];
        var errors = new List<string>();
        if (options.Accounts is null || options.Accounts.Count > 100) errors.Add("accounts");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (options.Accounts?.Count ?? 0); i++)
        {
            var account = options.Accounts![i];
            var field = $"accounts[{i}]";
            if (account is null) { errors.Add(field); continue; }
            if (!InputValidator.IsAccountId(account.Id) || !ids.Add(account.Id)) errors.Add(field + ".id");
            if (string.IsNullOrEmpty(account.Host) || account.Host.Length > 253 ||
                account.Host.Any(char.IsWhiteSpace) || account.Host.Any(char.IsControl) ||
                Uri.CheckHostName(account.Host) == UriHostNameType.Unknown) errors.Add(field + ".host");
            if (account.Port is < 1 or > 65535) errors.Add(field + ".port");
            if (!Enum.IsDefined(account.Security)) errors.Add(field + ".security");
            if (!Enum.IsDefined(account.Authentication)) errors.Add(field + ".authentication");
            if (string.IsNullOrWhiteSpace(account.Username) || account.Username.Length > 1024 || account.Username.Any(char.IsControl)) errors.Add(field + ".username");
            if (account.DisplayName is not null && (account.DisplayName.Length > 256 || account.DisplayName.Any(char.IsControl))) errors.Add(field + ".displayName");
            if (!ValidCredentialReference(account.CredentialReference)) errors.Add(field + ".credentialReference");
            if (account.Writes is { } writes)
            {
                if ((writes.Send || writes.CreateDraft || writes.FromAddress is not null)
                    && !InputValidator.IsBareAddress(writes.FromAddress ?? account.Username)) errors.Add(field + ".writes.fromAddress");
                if (!ValidWriteFolder(writes.TrashFolder)) errors.Add(field + ".writes.trashFolder");
                if (!ValidWriteFolder(writes.DraftsFolder)) errors.Add(field + ".writes.draftsFolder");
                if ((writes.Send || writes.Smtp is not null) && !ValidSmtp(writes.Smtp)) errors.Add(field + ".writes.smtp");
            }
            if (account.InboxCleanup is { } cleanup)
            {
                if (!System.Net.Mail.MailAddress.TryCreate(cleanup.Sender, out var sender)
                    || sender.Address != cleanup.Sender || cleanup.Sender.Any(char.IsWhiteSpace)
                    || cleanup.Sender.Any(char.IsControl) || cleanup.Sender.Length > 254)
                    errors.Add(field + ".inboxCleanup.sender");
                if (string.IsNullOrWhiteSpace(cleanup.TrashFolder) || cleanup.TrashFolder.Length > 1024
                    || cleanup.TrashFolder.Any(char.IsControl) || cleanup.TrashFolder.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
                    errors.Add(field + ".inboxCleanup.trashFolder");
            }
            if (account.AllowedPrivateNetworks is null || account.AllowedPrivateNetworks.Count > 32 ||
                account.AllowedPrivateNetworks.Any(network => !IPNetwork.TryParse(network, out _))) errors.Add(field + ".allowedPrivateNetworks");
        }
        if (options.SecretFiles is null || options.SecretFiles.Count > 100) errors.Add("secretFiles");
        else
        {
            if (options.SecretFiles.Count != 0 && (string.IsNullOrWhiteSpace(options.SecretRoot) || !Path.IsPathFullyQualified(options.SecretRoot))) errors.Add("secretRoot");
            foreach (var (alias, path) in options.SecretFiles)
            {
                if (!InputValidator.IsAccountId(alias) || string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
                    path.Contains(':') || path.Split('/', '\\').Any(segment => segment is ".." or "." or "") || path.Any(char.IsControl)) errors.Add("secretFiles.entry");
            }
        }
        if (options.Limits is null) errors.Add("limits");
        else ValidateLimits(options.Limits, errors);
        return errors;
    }

    private static bool ValidWriteFolder(string? name) => name is null ||
        (name.Length is > 0 and <= 1024 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
            && !name.Equals("INBOX", StringComparison.OrdinalIgnoreCase));

    private static bool ValidSmtp(SmtpSubmissionOptions? smtp) => smtp is not null
        && smtp.Host is { Length: > 0 and <= 253 } && !smtp.Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
        && Uri.CheckHostName(smtp.Host) != UriHostNameType.Unknown && smtp.Port is >= 1 and <= 65535
        && Enum.IsDefined(smtp.Security) && Enum.IsDefined(smtp.Authentication)
        && smtp.Username is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(smtp.Username) && !smtp.Username.Any(char.IsControl)
        && ValidCredentialReference(smtp.CredentialReference)
        && smtp.AllowedPrivateNetworks is { Count: <= 32 } && smtp.AllowedPrivateNetworks.All(network => IPNetwork.TryParse(network, out _));

    private static bool ValidCredentialReference(string? value)
    {
        if (value is null) return false;
        var split = value.IndexOf(':');
        if (split < 0) return false;
        var provider = value[..split];
        var key = value[(split + 1)..];
        return provider switch
        {
            "wincred" => key.StartsWith("obs-imap/", StringComparison.Ordinal) && InputValidator.IsAccountId(key[9..]),
            "file" => InputValidator.IsAccountId(key),
            "env" => key.Length is > 0 and <= 128 && (char.IsAsciiLetter(key[0]) || key[0] == '_') &&
                key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'),
            _ => false
        };
    }

    private static void ValidateLimits(ConnectorLimits limits, List<string> errors)
    {
        void Bound(string name, int value, int ceiling)
        {
            if (value < 1 || value > ceiling) errors.Add("limits." + name);
        }
        Bound(nameof(limits.MaxSearchPageSize), limits.MaxSearchPageSize, 200);
        Bound(nameof(limits.MaxFolderPageSize), limits.MaxFolderPageSize, 500);
        Bound(nameof(limits.UidIntervalWidth), limits.UidIntervalWidth, 10_000);
        Bound(nameof(limits.MaxSearchIntervals), limits.MaxSearchIntervals, 20);
        Bound(nameof(limits.MaxConnections), limits.MaxConnections, 8);
        Bound(nameof(limits.MaxQueuedRequests), limits.MaxQueuedRequests, 32);
        Bound(nameof(limits.ConnectTimeoutSeconds), limits.ConnectTimeoutSeconds, 120);
        Bound(nameof(limits.OperationTimeoutSeconds), limits.OperationTimeoutSeconds, 120);
        Bound(nameof(limits.IdleTimeoutSeconds), limits.IdleTimeoutSeconds, 600);
        Bound(nameof(limits.CursorLifetimeSeconds), limits.CursorLifetimeSeconds, 900);
        Bound(nameof(limits.MaxMessageBytes), limits.MaxMessageBytes, 25 * 1024 * 1024);
        Bound(nameof(limits.MaxPartWireBytes), limits.MaxPartWireBytes, 8 * 1024 * 1024);
        Bound(nameof(limits.MaxTextBytes), limits.MaxTextBytes, 256 * 1024);
        Bound(nameof(limits.MaxAttachmentBytes), limits.MaxAttachmentBytes, 2 * 1024 * 1024);
        Bound(nameof(limits.MaxResponseBytes), limits.MaxResponseBytes, 4 * 1024 * 1024);
        Bound(nameof(limits.MaxHeaderBytes), limits.MaxHeaderBytes, 64 * 1024);
        Bound(nameof(limits.MaxHeaderFieldBytes), limits.MaxHeaderFieldBytes, 8 * 1024);
        Bound(nameof(limits.MaxMimeParts), limits.MaxMimeParts, 200);
        Bound(nameof(limits.MaxMimeDepth), limits.MaxMimeDepth, 20);
        Bound(nameof(limits.MaxSecretBytes), limits.MaxSecretBytes, 16 * 1024);
        Bound(nameof(limits.MaxProtocolBytes), limits.MaxProtocolBytes, 16 * 1024 * 1024);
        if (limits.MaxHeaderFieldBytes > limits.MaxHeaderBytes) errors.Add("limits.headerRelationship");
        if (limits.MaxTextBytes > limits.MaxPartWireBytes) errors.Add("limits.textRelationship");
        if ((limits.MaxAttachmentBytes + 2L) / 3 * 4 + 4096 > limits.MaxResponseBytes) errors.Add("limits.attachmentResponseRelationship");
    }
}
