using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class WindowsCredentialTests
{
    [Theory]
    [InlineData("wincred:obs-imap/fixture", true)]
    [InlineData("wincred:obs-imap/", false)]
    [InlineData("wincred:other/JK", false)]
    [InlineData("wincred:obs-imap/../fixture", false)]
    public void Vault_references_are_restricted_to_connector_namespace(string reference, bool valid)
    {
        var options = new ConnectorOptions { Accounts = [new AccountOptions
        {
            Id = "JK", Host = "imap.example.test", Username = "user", CredentialReference = reference
        }] };
        Assert.Equal(valid, ConfigurationValidator.GetErrors(options).Count == 0);
    }

    [WindowsVaultFact]
    public async Task Native_vault_roundtrip_rotation_and_missing_credential()
    {
        var target = "obs-imap/test_" + Guid.NewGuid().ToString("N");
        var provider = new CredentialProvider(new());
        var missing = await Assert.ThrowsAsync<ConnectorException>(async () => await provider.GetCredentialAsync("wincred:" + target));
        Assert.Equal(ErrorCodes.AuthenticationFailed, missing.Code);
        WindowsCredentialStore.Write(target, "synthetic-user", "synthetic-æ🔒-password", overwrite: false);
        try
        {
            Assert.Equal("synthetic-æ🔒-password", await provider.GetCredentialAsync("wincred:" + target));
            Assert.Throws<ConnectorException>(() => WindowsCredentialStore.Write(target, "synthetic-user", "collision", overwrite: false));
            WindowsCredentialStore.Write(target, "synthetic-user", "rotated-value");
            Assert.Equal("rotated-value", await provider.GetCredentialAsync("wincred:" + target));
            Assert.Throws<ConnectorException>(() => WindowsCredentialStore.Write(target, "synthetic-user", new string('x', 1281)));
            Assert.Equal("rotated-value", await provider.GetCredentialAsync("wincred:" + target));
        }
        finally { WindowsCredentialStore.Delete(target); }
    }

    private sealed class WindowsVaultFactAttribute : FactAttribute
    {
        public WindowsVaultFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Native Windows Credential Manager requires Windows.";
        }
    }
}
