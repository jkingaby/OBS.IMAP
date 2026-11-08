using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

public sealed class CredentialProvider(ConnectorOptions options) : ICredentialProvider
{
    public async ValueTask<string> GetCredentialAsync(string credentialReference, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string value;
            if (credentialReference.StartsWith("env:", StringComparison.Ordinal))
                value = Environment.GetEnvironmentVariable(credentialReference[4..]) ?? "";
            else if (credentialReference.StartsWith("wincred:", StringComparison.Ordinal))
                value = WindowsCredentialStore.Read(credentialReference[8..]);
            else if (credentialReference.StartsWith("file:", StringComparison.Ordinal)
                && options.SecretRoot is { } secretRoot
                && options.SecretFiles.TryGetValue(credentialReference[5..], out var mapped))
            {
                var root = Path.GetFullPath(secretRoot);
                var path = Path.GetFullPath(Path.Combine(root, mapped));
                var relative = Path.GetRelativePath(root, path);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathRooted(relative)) throw new ConnectorException(ErrorCodes.InvalidConfiguration);
                // Reject links in every component, including root ancestors.
                for (FileSystemInfo? entry = new FileInfo(path); entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new ConnectorException(ErrorCodes.InvalidConfiguration);
                    if (OperatingSystem.IsWindows()) ValidateWindowsPermissions(entry, entry is FileInfo);
                    else
                    {
                        LinuxFileOwnership.Validate(entry.FullName);
                        if (entry is DirectoryInfo && (File.GetUnixFileMode(entry.FullName) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                            throw new ConnectorException(ErrorCodes.InvalidConfiguration);
                    }
                }
                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(path);
                    if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                        throw new ConnectorException(ErrorCodes.InvalidConfiguration);
                }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                if (stream.Length > options.Limits.MaxSecretBytes) throw new ConnectorException(ErrorCodes.InvalidConfiguration);
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(bytes, cancellationToken)) != 0)
                {
                    if (buffer.Length + read > options.Limits.MaxSecretBytes) throw new ConnectorException(ErrorCodes.InvalidConfiguration);
                    buffer.Write(bytes, 0, read);
                }
                value = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimEnd('\r', '\n');
                Array.Clear(bytes);
            }
            else throw new ConnectorException(ErrorCodes.InvalidConfiguration);
            if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > options.Limits.MaxSecretBytes)
                throw new ConnectorException(ErrorCodes.AuthenticationFailed);
            return value;
        }
        catch (ConnectorException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new ConnectorException(ErrorCodes.AuthenticationFailed); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void ValidateWindowsPermissions(FileSystemInfo entry, bool secret)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            identity.User!.Value, "S-1-5-18", "S-1-5-32-544",
            // Windows system-directory owner (TrustedInstaller).
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
        };
        FileSystemSecurity security = entry is FileInfo file ? file.GetAccessControl() : ((DirectoryInfo)entry).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner.Value))
            throw new ConnectorException(ErrorCodes.InvalidConfiguration);
        var sensitive = secret ? FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
            : FileSystemRights.WriteData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
              FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & sensitive) != 0 && !allowed.Contains(rule.IdentityReference.Value))
                throw new ConnectorException(ErrorCodes.InvalidConfiguration);
    }
}
