using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace RepoBackup.Core.Models;

public sealed record WslLink(string SourceId, string RelativePath, WslLinkData Data);

public sealed record WslLinkData(byte[] ReparseData, byte[] LegacyTarget, long CreationTime, long AccessTime, long WriteTime)
{
    public const uint Tag = 0xA000001D;
    public const int MaximumSize = 16 * 1024;
    [JsonIgnore] public string Fingerprint => Convert.ToHexString(SHA256.HashData([.. ReparseData, .. LegacyTarget])) + ":" + CreationTime + ":" + WriteTime;

    public void Validate()
    {
        if (ReparseData is null || LegacyTarget is null || ReparseData.Length is < 12 or > MaximumSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(ReparseData) != Tag ||
            BinaryPrimitives.ReadUInt16LittleEndian(ReparseData.AsSpan(4)) != ReparseData.Length - 8 ||
            BinaryPrimitives.ReadUInt16LittleEndian(ReparseData.AsSpan(6)) != 0)
            throw new InvalidDataException("Invalid WSL link reparse buffer.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(ReparseData.AsSpan(8));
        ReadOnlySpan<byte> target;
        if (version == 1 && ReparseData.Length == 12 && LegacyTarget.Length <= MaximumSize) target = LegacyTarget;
        else if (version == 2 && LegacyTarget.Length == 0) target = ReparseData.AsSpan(12);
        else throw new InvalidDataException("Unsupported WSL link version or target data.");
        if (target.IsEmpty || target.Contains((byte)0)) throw new InvalidDataException("Invalid WSL link target.");
        try
        {
            _ = DateTime.FromFileTimeUtc(CreationTime); _ = DateTime.FromFileTimeUtc(AccessTime); _ = DateTime.FromFileTimeUtc(WriteTime);
        }
        catch (ArgumentOutOfRangeException e)
        { throw new InvalidDataException("Invalid WSL link target or timestamps.", e); }
    }
}
