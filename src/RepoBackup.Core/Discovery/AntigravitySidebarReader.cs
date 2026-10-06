using System.Text;

namespace RepoBackup.Core.Discovery;

// Antigravity's unified state stores a base64 protobuf map. Only its workspace URI keys are needed.
public static class AntigravitySidebarReader
{
    public static List<string> ReadWorkspaceUris(string encoded)
    {
        if (encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException("Sidebar workspace state is too large.");
        byte[] data;
        try { data = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidDataException("Invalid sidebar workspace encoding."); }
        var reader = new WireReader(data); var result = new List<string>();
        while (reader.Next(out var field, out var wire, out var bytes))
        {
            if (field != 1) continue;
            if (wire != 2) throw new InvalidDataException("Invalid sidebar workspace map.");
            var entry = new WireReader(bytes); string? key = null; var hasRow = false;
            while (entry.Next(out var entryField, out var entryWire, out var value))
            {
                if (entryField == 1)
                {
                    if (entryWire != 2) throw new InvalidDataException("Invalid workspace URI.");
                    try { key = new UTF8Encoding(false, true).GetString(value); }
                    catch (DecoderFallbackException) { throw new InvalidDataException("Invalid workspace URI encoding."); }
                }
                if (entryField == 2 && entryWire == 2) hasRow = true;
            }
            if (key is null || !hasRow) throw new InvalidDataException("Incomplete sidebar workspace entry.");
            result.Add(key);
            if (result.Count > 10000) throw new InvalidDataException("Too many sidebar workspaces.");
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private ref struct WireReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int offset;

        public bool Next(out int field, out int wire, out ReadOnlySpan<byte> bytes)
        {
            field = wire = 0; bytes = default;
            if (offset == data.Length) return false;
            var tag = Varint();
            if (tag > uint.MaxValue || tag >> 3 == 0) throw new InvalidDataException("Invalid protobuf field.");
            field = (int)(tag >> 3); wire = (int)(tag & 7);
            var length = wire switch
            {
                0 => -1, 1 => 8, 2 => CheckedLength(Varint()), 5 => 4,
                _ => throw new InvalidDataException("Unsupported protobuf wire type.")
            };
            if (length == -1) { Varint(); return true; }
            if (length > data.Length - offset) throw new InvalidDataException("Truncated protobuf value.");
            bytes = data.Slice(offset, length); offset += length; return true;
        }

        private static int CheckedLength(ulong length) => length <= int.MaxValue ? (int)length : throw new InvalidDataException("Invalid protobuf length.");

        private ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (offset == data.Length) throw new InvalidDataException("Truncated protobuf varint.");
                var next = data[offset++];
                if (shift == 63 && next > 1) throw new InvalidDataException("Overflowed protobuf varint.");
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid protobuf varint.");
        }
    }
}
