using System.Buffers.Binary;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Windows;

// Always open the entry itself. A Linux target need not exist or be accessible from Windows.
public static partial class WslLinks
{
    public static WslLinkData? Read(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0) return null;
        using var handle = Open(path, 0x80000000, 3);
        var buffer = new byte[WslLinkData.MaximumSize];
        if (!DeviceIoControl(handle, GetReparsePoint, null, 0, buffer, buffer.Length, out var count, IntPtr.Zero)) throw Failure(path);
        if (count < 8) throw new InvalidDataException("Truncated reparse point: " + path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer) != WslLinkData.Tag) return null;
        if ((attributes & FileAttributes.Directory) != 0) throw new InvalidDataException("A WSL symbolic link must be a file entry: " + path);
        if (!GetFileTime(handle, out var creation, out var access, out var write)) throw Failure(path);
        byte[] legacy = [];
        if (count >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)) == 1)
        {
            if (!GetFileSizeEx(handle, out var size)) throw Failure(path);
            if (size is < 1 or > WslLinkData.MaximumSize) throw new InvalidDataException("Invalid legacy WSL link target length: " + path);
            legacy = new byte[(int)size];
            var offset = 0;
            while (offset < legacy.Length)
            {
                var read = RandomAccess.Read(handle, legacy.AsSpan(offset), offset);
                if (read == 0) throw new IOException("WSL link changed while reading: " + path);
                offset += read;
            }
        }
        var data = new WslLinkData(buffer[..count], legacy, creation, access, write);
        data.Validate(); return data;
    }

    // Resolve ancestors for containment, but preserve a WSL leaf rather than following it.
    public static string SourcePath(string path)
    {
        // Recovery must not require old drives or network shares to be accessible.
        // Preserve PhysicalPath's existing behavior for unavailable source entries.
        if (!File.Exists(path) && !Directory.Exists(path)) return PathSafety.PhysicalPath(path);
        try
        {
            if (Read(path) is not null)
                return Path.Combine(PathSafety.PhysicalPath(Path.GetDirectoryName(PathSafety.Normalize(path))!), Path.GetFileName(path));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return PathSafety.PhysicalPath(path);
    }

    public static void Create(string path, WslLinkData data, string restoreRoot)
    {
        data.Validate();
        var full = PathSafety.Normalize(path); var root = PathSafety.Normalize(restoreRoot);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || !PathSafety.IsWithin(full, root)) throw new InvalidDataException("WSL link would escape the restore directory.");
        // Check every ancestor, including the restore root's ancestors. Never write through a link.
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(full); parent is not null; parent = Path.GetDirectoryName(parent)) parents.Push(parent);
        foreach (var parent in parents)
        {
            try
            {
                var attributes = File.GetAttributes(parent);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("WSL link has an unsafe restore ancestor: " + parent);
            }
            catch (DirectoryNotFoundException) { Directory.CreateDirectory(parent); }
            catch (FileNotFoundException) { Directory.CreateDirectory(parent); }
        }
        // CREATE_NEW refuses existing files, directories, and dangling links.
        using (var handle = Open(full, 0x40000000, 1))
        {
            if (data.LegacyTarget.Length > 0) RandomAccess.Write(handle, data.LegacyTarget, 0);
            if (!DeviceIoControl(handle, SetReparsePoint, data.ReparseData, data.ReparseData.Length, null, 0, out _, IntPtr.Zero)) throw Failure(full);
            var creation = data.CreationTime; var access = data.AccessTime; var write = data.WriteTime;
            if (!SetFileTime(handle, ref creation, ref access, ref write)) throw Failure(full);
        }
        Verify(full, data);
    }

    public static void Verify(string path, WslLinkData expected)
    {
        if (Read(path)?.Fingerprint != expected.Fingerprint) throw new IOException("Restored WSL link does not match the snapshot: " + path);
    }
}
