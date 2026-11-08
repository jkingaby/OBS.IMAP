namespace OBS.IMAP.Core;

public enum ImapSecurityMode { SslOnConnect, StartTls }
public enum ImapAuthenticationMode { Password, OAuth2 }

public sealed record AccountOptions
{
    public string Id { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 993;
    public ImapSecurityMode Security { get; init; } = ImapSecurityMode.SslOnConnect;
    public string Username { get; init; } = "";
    public ImapAuthenticationMode Authentication { get; init; } = ImapAuthenticationMode.Password;
    public string CredentialReference { get; init; } = "";
    public InboxCleanupOptions? InboxCleanup { get; init; }
    public MailWriteOptions? Writes { get; init; }
    // Exceptions apply only to this account's exact configured host and port.
    public IReadOnlyList<string> AllowedPrivateNetworks { get; init; } = [];
}

public sealed record MailWriteOptions
{
    public bool Move { get; init; }
    public bool Trash { get; init; }
    public bool CreateDraft { get; init; }
    public bool Send { get; init; }
    public bool RequireSendConfirmation { get; init; } = true;
    public string? TrashFolder { get; init; }
    public string? DraftsFolder { get; init; }
    public string? FromAddress { get; init; }
    public SmtpSubmissionOptions? Smtp { get; init; }
}

public sealed record SmtpSubmissionOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 465;
    public ImapSecurityMode Security { get; init; } = ImapSecurityMode.SslOnConnect;
    public string Username { get; init; } = "";
    public ImapAuthenticationMode Authentication { get; init; } = ImapAuthenticationMode.Password;
    public string CredentialReference { get; init; } = "";
    public IReadOnlyList<string> AllowedPrivateNetworks { get; init; } = [];
}

public sealed record ConnectorOptions
{
    public bool EnableInboxCleanup { get; init; }
    public IReadOnlyList<AccountOptions> Accounts { get; init; } = [];
    public ConnectorLimits Limits { get; init; } = new();
    public string? SecretRoot { get; init; }
    public IReadOnlyDictionary<string, string> SecretFiles { get; init; } = new Dictionary<string, string>();
}

// Local operator opt-in only. No tool argument or email can grant this permission.
public sealed record InboxCleanupOptions
{
    public bool Enabled { get; init; }
    public string Sender { get; init; } = "";
    public string TrashFolder { get; init; } = "Trash";
}

public sealed record InboxCleanupPage(int Matched, int Moved, string? Continuation);

public sealed record ConnectorLimits
{
    public int MaxSearchPageSize { get; init; } = 200;
    public int MaxFolderPageSize { get; init; } = 500;
    public int UidIntervalWidth { get; init; } = 10_000;
    public int MaxSearchIntervals { get; init; } = 20;
    public int MaxConnections { get; init; } = 8;
    public int MaxQueuedRequests { get; init; } = 32;
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public int OperationTimeoutSeconds { get; init; } = 30;
    public int IdleTimeoutSeconds { get; init; } = 120;
    public int CursorLifetimeSeconds { get; init; } = 900;
    public int MaxMessageBytes { get; init; } = 25 * 1024 * 1024;
    public int MaxPartWireBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxTextBytes { get; init; } = 256 * 1024;
    public int MaxAttachmentBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxHeaderBytes { get; init; } = 64 * 1024;
    public int MaxHeaderFieldBytes { get; init; } = 8 * 1024;
    public int MaxMimeParts { get; init; } = 200;
    public int MaxMimeDepth { get; init; } = 20;
    public int MaxSecretBytes { get; init; } = 16 * 1024;
    public int MaxProtocolBytes { get; init; } = 16 * 1024 * 1024;
}
