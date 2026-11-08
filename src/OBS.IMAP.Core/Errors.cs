namespace OBS.IMAP.Core;

public static class ErrorCodes
{
    public const string InvalidInput = "invalid_input";
    public const string AccessDenied = "access_denied";
    public const string NotFound = "not_found";
    public const string StaleReference = "stale_reference";
    public const string InvalidCursor = "invalid_cursor";
    public const string LimitExceeded = "limit_exceeded";
    public const string Timeout = "timeout";
    public const string AuthenticationFailed = "authentication_failed";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string InvalidConfiguration = "invalid_configuration";
    public const string ConfirmationRequired = "confirmation_required";
    public const string ConfirmationDeclined = "confirmation_declined";
    public const string WriteOutcomeUnknown = "write_outcome_unknown";
}

public sealed class ConnectorException : Exception
{
    private static readonly IReadOnlySet<string> AllowedCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ErrorCodes.InvalidInput, ErrorCodes.AccessDenied, ErrorCodes.NotFound, ErrorCodes.StaleReference,
        ErrorCodes.InvalidCursor, ErrorCodes.LimitExceeded, ErrorCodes.Timeout, ErrorCodes.AuthenticationFailed,
        ErrorCodes.ProviderUnavailable, ErrorCodes.InvalidConfiguration, ErrorCodes.ConfirmationRequired,
        ErrorCodes.ConfirmationDeclined, ErrorCodes.WriteOutcomeUnknown
    };

    public ConnectorException(string code) : base(AllowedCodes.Contains(code) ? code : ErrorCodes.ProviderUnavailable)
    {
        Code = Message;
    }

    public string Code { get; }
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
}
