namespace OBS.IMAP.Host;

public sealed class LocalOptions
{
    public string[]? AllowedAccountIds { get; set; }
}

public sealed class RemoteOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string ResourceUrl { get; set; } = "";
    public string RequiredScope { get; set; } = "mail.read";
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string[] AllowedHosts { get; set; } = [];
    public string[] AllowedOrigins { get; set; } = [];
    public RemoteGrant[] Grants { get; set; } = [];

    public void Validate()
    {
        if (!IsHttps(Issuer) || !IsHttps(ResourceUrl) || string.IsNullOrWhiteSpace(Audience)
            || Audience.Length > 2048 || !Uri.TryCreate(ResourceUrl, UriKind.Absolute, out var resource)
            || resource.AbsolutePath != "/mcp" || !string.IsNullOrEmpty(resource.Query)
            || !string.IsNullOrEmpty(resource.Fragment) || string.IsNullOrWhiteSpace(RequiredScope)
            || RequiredScope.Any(c => c < 0x21 || c > 0x7e || c is '"' or '\\') || Port is < 0 or > 65535
            || !System.Net.IPAddress.TryParse(ListenAddress, out _)
            || AllowedHosts is null || AllowedHosts.Length == 0 || AllowedHosts.Any(h => string.IsNullOrWhiteSpace(h) || h.Contains('*') || h.Contains('/') || h.Contains(':'))
            || AllowedOrigins is null || AllowedOrigins.Any(o => !IsHttps(o))
            || Grants is null || Grants.Any(g => g is null || string.IsNullOrWhiteSpace(g.Subject) || g.Subject.Length > 512 || g.AccountIds is null)
            || Grants.Select(g => g.Subject).Distinct(StringComparer.Ordinal).Count() != Grants.Length)
            throw new InvalidOperationException("Invalid remote configuration. HTTPS issuer/resource, explicit hosts, unique subject grants and valid listener settings are required.");
    }

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.Query);
}

public sealed class RemoteGrant
{
    public string Subject { get; set; } = "";
    public string[] AccountIds { get; set; } = [];
}
