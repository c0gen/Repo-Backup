using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepoBackup.Core.Windows;

public static partial class WslLinks
{
    private const uint GetReparsePoint = 0x900A8, SetReparsePoint = 0x900A4;
    private static SafeFileHandle Open(string path, uint access, uint disposition)
    {
        var full = Path.GetFullPath(path);
        var extended = full.StartsWith(@"\\?\") ? full : full.StartsWith(@"\\") ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var handle = CreateFileW(extended, access, 7, IntPtr.Zero, disposition, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw Failure(path, error); }
        return handle;
    }
    private static IOException Failure(string path, int? error = null) => new("Cannot preserve WSL link '" + path + "': " + new Win32Exception(error ?? Marshal.GetLastWin32Error()).Message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTime(SafeFileHandle handle, out long creation, out long access, out long write);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle handle, ref long creation, ref long access, ref long write);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);
}
