using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Aeris;

public static class FileSystemPaths
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

// statx has a fixed ABI on Linux x64 and arm64, unlike struct stat.
internal static class LinuxFiles
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Info
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        public readonly string Device => $"{DeviceMajor:X}:{DeviceMinor:X}";
        public readonly string Identity => $"{Device}:{Inode:X}";
        public readonly bool IsRegular => (Mode & 0xf000) == 0x8000;
        public readonly long CreatedTicks => (Mask & 0x800) != 0
            ? DateTime.UnixEpoch.AddSeconds(BirthSeconds).Ticks + BirthNanoseconds / 100
            : DateTime.UnixEpoch.AddSeconds(ChangeSeconds).Ticks + ChangeNanoseconds / 100;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Stat(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Info info);

    internal static Info Read(string path, int descriptor = -100)
    {
        if (Stat(descriptor, path, path.Length == 0 ? 0x1000 : 0x100, 0xfff, out var info) != 0 || (info.Mask & 0x100) == 0)
            throw new IOException($"statx: {path}", new Win32Exception(Marshal.GetLastPInvokeError()));
        return info;
    }
}
