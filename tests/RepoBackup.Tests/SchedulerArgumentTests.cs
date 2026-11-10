using System.Runtime.InteropServices;
using System.Xml.Linq;
using RepoBackup.Core.Models;
using RepoBackup.Core.Windows;
using static RepoBackup.Tests.TestRunner;

namespace RepoBackup.Tests;

public static class SchedulerArgumentTests
{
    public static async Task RunAsync(TestRunner tests)
    {
        await tests.Run("Windows parses scheduler arguments with spaces, Unicode, UNC and trailing separators", () =>
        {
            var schedule = new ScheduleDefinition { DestinationId = "fixture" };
            foreach (var path in new[] { @"C:\Fixture", @"C:\Fixture space\", @"C:\项目\catalog\", @"C:\", @"\\server\share\folder\" })
            {
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var xml = XDocument.Parse(WindowsScheduler.BuildXml(schedule, @"C:\Fixture\runner.exe", path, "S-1-5-21-1"));
                var arguments = xml.Descendants(ns + "Arguments").Single().Value;
                var buffer = CommandLineToArgvW("runner.exe " + arguments, out var count);
                if (buffer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                try
                {
                    var parsed = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size))).ToArray();
                    Assert(parsed.SequenceEqual(new[] { "runner.exe", "--data-dir", path, "run-schedule", "--id", schedule.Id }), "Scheduled command lost its catalog or command: " + path);
                }
                finally { LocalFree(buffer); }
            }
            return Task.CompletedTask;
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
