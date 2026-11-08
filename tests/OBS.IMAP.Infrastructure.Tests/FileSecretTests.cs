using System.Security.AccessControl;
using System.Security.Principal;
using OBS.IMAP.Core;
using Xunit;

namespace OBS.IMAP.Infrastructure.Tests;

public sealed class FileSecretTests
{
    [Fact]
    public async Task Protected_file_secret_is_read_and_broad_read_permissions_are_denied()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "secret-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "credential");
        await File.WriteAllTextAsync(file, "sentinel-file-secret\r\n");
        try
        {
            if (OperatingSystem.IsWindows()) SetWindowsAccess(file, false);
            else File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var provider = new CredentialProvider(new ConnectorOptions
            {
                SecretRoot = root,
                SecretFiles = new Dictionary<string, string> { ["mail"] = "credential" }
            });
            if (OperatingSystem.IsWindows())
                CredentialProvider.ValidateWindowsPermissions(new FileInfo(file), true);
            else Assert.Equal("sentinel-file-secret", await provider.GetCredentialAsync("file:mail"));
            if (OperatingSystem.IsWindows()) SetWindowsAccess(file, true);
            else File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Equal(ErrorCodes.InvalidConfiguration, (await Assert.ThrowsAsync<ConnectorException>(async () =>
                await provider.GetCredentialAsync("file:mail"))).Code);
        }
        finally { File.Delete(file); Directory.Delete(root); }
    }

    [Fact]
    public void Linux_ownership_accepts_only_effective_user_or_root()
    {
        Assert.True(LinuxFileOwnership.IsTrustedOwner(0, 1000));
        Assert.True(LinuxFileOwnership.IsTrustedOwner(1000, 1000));
        Assert.False(LinuxFileOwnership.IsTrustedOwner(1001, 1000));
        Assert.False(LinuxFileOwnership.IsTrustedOwner(1001, 0));
        Assert.True(LinuxFileOwnership.IsRegularFileOrDirectory(0x8180)); // regular0600
        Assert.True(LinuxFileOwnership.IsRegularFileOrDirectory(0x41c0)); // directory0700
        foreach (var special in new ushort[] { 0x1180, 0x2180, 0x6180, 0xa180, 0xc180 })
            Assert.False(LinuxFileOwnership.IsRegularFileOrDirectory(special)); // FIFO/device/link/socket
        if (OperatingSystem.IsLinux()) LinuxFileOwnership.Validate(AppContext.BaseDirectory);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SetWindowsAccess(string path, bool expose)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        if (expose) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.ReadData, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
