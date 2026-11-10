using System.Diagnostics;
using System.Text;
using RepoBackup.Core.Backup;
using RepoBackup.Core.Infrastructure;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class SnapshotSummaryTests
{
    public static async Task RunAsync(TestRunner tests)
    {
        await tests.Run("Snapshot summaries skip five million paths with bounded managed memory", async () =>
        {
            GC.Collect(); var baseline = GC.GetTotalMemory(true);
            using var stream = new LargeSnapshotStream(5_000_000);
            var snapshots = await SnapshotSummaryReader.ReadAsync(stream);
            Assert(snapshots.Count == 1 && snapshots[0].Id == "snapshot" && snapshots[0].Successful && snapshots[0].Hostname == "fixture",
                "Summary identity or tags were lost.");
            Assert(stream.PathsRead == 5_000_000 && stream.PeakMemory - baseline < 64L * 1024 * 1024,
                $"Snapshot paths were buffered: peak managed growth {stream.PeakMemory - baseline:N0} bytes.");
        });
        await tests.Run("Snapshot summary parsing handles null, unknown fields, Unicode and cancellation", async () =>
        {
            using var empty = new MemoryStream(Encoding.UTF8.GetBytes("null"));
            Assert((await SnapshotSummaryReader.ReadAsync(empty)).Count == 0, "Legacy null listing rejected.");
            using var sample = new MemoryStream(Encoding.UTF8.GetBytes("[{\"id\":\"unicode\",\"time\":\"2026-10-15T00:00:00Z\",\"hostname\":\"文件\",\"tags\":[\"successful\"],\"unknown\":{\"paths\":[\"x\"]},\"paths\":[\"résumé\"]}]"));
            Assert((await SnapshotSummaryReader.ReadAsync(sample)).Single().Hostname == "文件", "Unicode summary changed.");
            using var cancellation = new CancellationTokenSource();
            using var stream = new LargeSnapshotStream(5_000_000, () => cancellation.Cancel());
            await Throws<OperationCanceledException>(() => SnapshotSummaryReader.ReadAsync(stream, cancellation.Token));
        });
        await tests.Run("Stream consumer failures kill and reap the native process", async () =>
        {
            var pid = 0; var watch = Stopwatch.StartNew();
            await Throws<InvalidDataException>(() => new ProcessRunner().RunStreamAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine($PID); [Console]::Out.Flush(); Start-Sleep -Seconds 30"],
                async (stream, token) =>
                {
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    pid = int.Parse((await reader.ReadLineAsync(token))!); throw new InvalidDataException("Injected parser failure");
                }));
            Assert(pid > 0 && watch.Elapsed < TimeSpan.FromSeconds(10), "Parser failure left a blocked process.");
            try { using var process = Process.GetProcessById(pid); Assert(process.HasExited, "Streamed process is still running."); }
            catch (ArgumentException) { }
        });
    }

    private sealed class LargeSnapshotStream(int paths, Action? cancel = null) : Stream
    {
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("[{\"id\":\"snapshot\",\"time\":\"2026-10-15T00:00:00Z\",\"hostname\":\"fixture\",\"tags\":[\"successful\"],\"paths\":[");
        private static readonly byte[] Item = Encoding.UTF8.GetBytes("\"/C/projects/large/file.txt\",");
        private static readonly byte[] Suffix = Encoding.UTF8.GetBytes("\"/last\"]}]");
        private int phase, offset;
        private long reads;
        public int PathsRead { get; private set; }
        public long PeakMemory { get; private set; }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            var written = 0;
            while (written < buffer.Length && phase < 3)
            {
                var segment = phase switch { 0 => Prefix, 1 => Item, _ => Suffix };
                var count = Math.Min(buffer.Length - written, segment.Length - offset);
                segment.AsSpan(offset, count).CopyTo(buffer[written..]); written += count; offset += count;
                if (offset != segment.Length) continue;
                offset = 0;
                if (phase == 1) { PathsRead++; if (PathsRead == paths) phase++; }
                else phase++;
            }
            if (++reads % 128 == 0) PeakMemory = Math.Max(PeakMemory, GC.GetTotalMemory(false));
            return written;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (reads > 3) cancel?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
