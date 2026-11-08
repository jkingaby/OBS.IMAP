using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

// Generic credentials belong to the Windows user running the connector.
public static class WindowsCredentialStore
{
    public static void ValidateTarget(string target)
    {
        if (!target.StartsWith("obs-imap/", StringComparison.Ordinal) || !InputValidator.IsAccountId(target[9..]))
            throw new ConnectorException(ErrorCodes.InvalidConfiguration);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager requires Windows.");
    }

    public static string Read(string target)
    {
        ValidateTarget(target);
        if (!CredRead(target, 1, 0, out var pointer))
            throw new ConnectorException(ErrorCodes.AuthenticationFailed);
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize is 0 or > 2560 || credential.BlobSize % 2 != 0)
                throw new ConnectorException(ErrorCodes.AuthenticationFailed);
            return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2)!;
        }
        finally
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize <= 2560)
                for (var i = 0; i < credential.BlobSize; i++) Marshal.WriteByte(credential.Blob, i, 0);
            CredFree(pointer);
        }
    }

    public static void Write(string target, string username, string password, bool overwrite = true)
    {
        ValidateTarget(target);
        if (!overwrite)
        {
            if (CredRead(target, 1, 0, out var existing))
            {
                CredFree(existing);
                throw new ConnectorException(ErrorCodes.InvalidConfiguration);
            }
            if (Marshal.GetLastWin32Error() != 1168) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
        }
        if (string.IsNullOrWhiteSpace(password) || Encoding.Unicode.GetByteCount(password) > 2560 || password.Contains('\0'))
            throw new ConnectorException(ErrorCodes.InvalidInput);
        var bytes = Encoding.Unicode.GetBytes(password);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = 1, Target = target, UserName = username,
                BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeHGlobal(pointer);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static void Delete(string target)
    {
        ValidateTarget(target);
        if (!CredDelete(target, 1, 0)) throw new ConnectorException(ErrorCodes.ProviderUnavailable);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? Target;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
