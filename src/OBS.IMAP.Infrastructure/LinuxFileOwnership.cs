using System.Runtime.InteropServices;
using OBS.IMAP.Core;

namespace OBS.IMAP.Infrastructure;

internal static class LinuxFileOwnership
{
    // Linux statx has a fixed cross-architecture 256-byte UAPI layout.
    // Request only UID and type; AT_SYMLINK_NOFOLLOW prevents resolving a substituted link.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Stat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx data);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUid();

    public static void Validate(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new ConnectorException(ErrorCodes.InvalidConfiguration);
        try
        {
            if (Stat(-100, path, 0x100, 0x9, out var data) != 0 || (data.Mask & 0x9) != 0x9
                || !IsRegularFileOrDirectory(data.Mode) || !IsTrustedOwner(data.Uid, EffectiveUid()))
                throw new ConnectorException(ErrorCodes.InvalidConfiguration);
        }
        catch (ConnectorException) { throw; }
        catch { throw new ConnectorException(ErrorCodes.InvalidConfiguration); }
    }

    internal static bool IsTrustedOwner(uint owner, uint effectiveUser) => owner == 0 || owner == effectiveUser;
    internal static bool IsRegularFileOrDirectory(ushort mode) => (mode & 0xf000) is 0x8000 or 0x4000;
}
