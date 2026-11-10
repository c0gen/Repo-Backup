using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RepoBackup.Core.Models;

namespace RepoBackup.Tests;

// Independent native fixture creation, so capture tests do not rely on the production writer.
internal static class WslLinkFixtures
{
    public static SafeFileHandle Lock(string path)
    {
        var handle = CreateFileW(path, 0x80000000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }

    public static void Create(string path, string target, uint version = 2)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = Encoding.UTF8.GetBytes(target);
        var buffer = new byte[12 + (version == 1 ? 0 : text.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, WslLinkData.Tag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(buffer.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), version);
        if (version != 1) text.CopyTo(buffer, 12);
        using var handle = CreateFileW(path, 0xC0000000, 7, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (version == 1) RandomAccess.Write(handle, text, 0);
        if (!DeviceIoControl(handle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int size, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
