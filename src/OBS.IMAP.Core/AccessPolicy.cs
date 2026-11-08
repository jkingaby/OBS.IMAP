using System.Collections.Frozen;

namespace OBS.IMAP.Core;

public sealed class RequestIdentity
{
    private readonly FrozenSet<string> allowedAccounts;

    public RequestIdentity(string issuer, string subject, IEnumerable<string> allowedAccounts, bool allowWrites = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(allowedAccounts);
        Issuer = issuer;
        Subject = subject;
        this.allowedAccounts = allowedAccounts.ToFrozenSet(StringComparer.Ordinal);
        AllowWrites = allowWrites;
    }

    public string Issuer { get; }
    public string Subject { get; }
    public string PrincipalId => $"{Issuer.Length}:{Issuer}{Subject}";
    public IReadOnlySet<string> AllowedAccounts => allowedAccounts;
    public bool AllowWrites { get; }
    public bool CanAccess(string accountId) => allowedAccounts.Contains(accountId);
}

public sealed class AccountCatalog
{
    private readonly FrozenDictionary<string, AccountOptions> accounts;

    public AccountCatalog(ConnectorOptions options)
    {
        ConfigurationValidator.Validate(options);
        accounts = options.Accounts.ToFrozenDictionary(account => account.Id, StringComparer.Ordinal);
    }

    public AccountOptions RequireAccount(RequestIdentity identity, string accountId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        InputValidator.AccountId(accountId);
        if (!identity.CanAccess(accountId) || !accounts.TryGetValue(accountId, out var account))
            throw new ConnectorException(ErrorCodes.AccessDenied);
        return account;
    }

    public IReadOnlyList<AccountInfo> ListAccounts(RequestIdentity identity) => accounts.Values
        .Where(account => identity.CanAccess(account.Id))
        .OrderBy(account => account.Id, StringComparer.Ordinal)
        .Select(account => new AccountInfo(account.Id, account.DisplayName ?? account.Id)).ToArray();
}
