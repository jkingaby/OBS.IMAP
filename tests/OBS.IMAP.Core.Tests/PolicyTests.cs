using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Core.Tests;

public sealed class PolicyTests
{
    private static AccountOptions Account(string id = "personal") => new()
    {
        Id = id, Host = "imap.example.test", Username = "user@example.test", CredentialReference = "env:IMAP_PASSWORD"
    };

    [Fact]
    public void Default_limits_and_valid_account_are_accepted()
    {
        ConfigurationValidator.Validate(new ConnectorOptions { Accounts = [Account()] });
    }

    [Fact]
    public void Identity_copies_allowlist_and_catalog_filters_account_metadata()
    {
        var allowed = new List<string> { "personal" };
        var identity = new RequestIdentity("issuer", "subject", allowed);
        allowed.Add("business");
        var catalog = new AccountCatalog(new ConnectorOptions { Accounts = [Account(), Account("business")] });
        Assert.Equal("personal", Assert.Single(catalog.ListAccounts(identity)).Id);
        Assert.Equal(ErrorCodes.AccessDenied, Assert.Throws<ConnectorException>(() => catalog.RequireAccount(identity, "business")).Code);
        Assert.Equal(ErrorCodes.AccessDenied, Assert.Throws<ConnectorException>(() => catalog.RequireAccount(identity, "unknown")).Code);
    }

    [Fact]
    public void Principal_binding_is_unambiguous_and_account_ids_are_case_sensitive()
    {
        Assert.NotEqual(new RequestIdentity("ab", "c", []).PrincipalId, new RequestIdentity("a", "bc", []).PrincipalId);
        Assert.False(new RequestIdentity("i", "s", ["personal"]).CanAccess("Personal"));
    }

    [Theory]
    [InlineData("../account")]
    [InlineData("account\r\n")]
    [InlineData("account\n")]
    [InlineData("")]
    [InlineData("åccount")]
    public void Invalid_account_identifiers_are_rejected(string accountId)
    {
        Assert.Equal(ErrorCodes.InvalidInput, Assert.Throws<ConnectorException>(() => InputValidator.AccountId(accountId)).Code);
    }

    [Fact]
    public void Configuration_errors_do_not_include_operator_values()
    {
        const string sentinel = "secret-value-do-not-log";
        var options = new ConnectorOptions { Accounts = [Account() with { Host = "https://" + sentinel, CredentialReference = sentinel }] };
        var errors = ConfigurationValidator.GetErrors(options);
        Assert.Contains("accounts[0].host", errors);
        Assert.Contains("accounts[0].credentialReference", errors);
        Assert.DoesNotContain(sentinel, string.Join(',', errors));
        Assert.DoesNotContain(sentinel, Assert.Throws<ConnectorException>(() => ConfigurationValidator.Validate(options)).ToString());
    }

    [Fact]
    public void Duplicate_accounts_invalid_enums_and_unlimited_limits_are_rejected()
    {
        var options = new ConnectorOptions
        {
            Accounts = [Account(), Account() with { Security = (ImapSecurityMode)99, Authentication = (ImapAuthenticationMode)99 }],
            Limits = new ConnectorLimits { MaxSearchPageSize = int.MaxValue, OperationTimeoutSeconds = 0 }
        };
        var errors = ConfigurationValidator.GetErrors(options);
        Assert.Contains("accounts[1].id", errors);
        Assert.Contains("accounts[1].security", errors);
        Assert.Contains("accounts[1].authentication", errors);
        Assert.Contains("limits.MaxSearchPageSize", errors);
        Assert.Contains("limits.OperationTimeoutSeconds", errors);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder/../secret")]
    [InlineData("/absolute/secret")]
    [InlineData("secret:stream")]
    public void File_secret_traversal_and_absolute_paths_are_rejected(string path)
    {
        var options = new ConnectorOptions { SecretRoot = Path.GetFullPath("secrets"), SecretFiles = new Dictionary<string, string> { ["mail"] = path } };
        Assert.Contains("secretFiles.entry", ConfigurationValidator.GetErrors(options));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    public void Zero_uid_and_validity_are_rejected(uint validity, uint uid)
    {
        Assert.Throws<ConnectorException>(() => InputValidator.Message(new MessageReference("personal", "INBOX", validity, uid)));
    }

    [Theory]
    [InlineData("1) UID FETCH 1")]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("../1")]
    [InlineData("1\n")]
    public void Mime_part_identifier_injection_is_rejected(string part)
    {
        Assert.Throws<ConnectorException>(() => InputValidator.PartId(part));
    }

    [Fact]
    public void Invalid_filter_ranges_and_oversized_queries_are_rejected()
    {
        var request = new SearchRequest("personal", "INBOX", new SearchFilter { ReceivedAfter = new DateOnly(2026, 1, 1), ReceivedBefore = new DateOnly(2025, 1, 1) });
        Assert.Throws<ConnectorException>(() => InputValidator.Search(request, new ConnectorLimits()));
        Assert.Throws<ConnectorException>(() => InputValidator.Search(request with { Filter = new SearchFilter { Text = new string('x', 1025) } }, new ConnectorLimits()));
        Assert.Throws<ConnectorException>(() => InputValidator.Search(request with { Filter = null, PageSize = 201 }, new ConnectorLimits()));
    }

    [Fact]
    public void Error_constructor_never_exposes_unknown_messages()
    {
        var error = new ConnectorException("mail server returned password sentinel");
        Assert.Equal(ErrorCodes.ProviderUnavailable, error.Code);
        Assert.DoesNotContain("sentinel", error.ToString());
        Assert.Equal(32, error.CorrelationId.Length);
    }
}
