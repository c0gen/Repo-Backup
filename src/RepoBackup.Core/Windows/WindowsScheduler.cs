using System.Security.Principal;
using System.Xml.Linq;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;
using RepoBackup.Core.Storage;

namespace RepoBackup.Core.Windows;

public sealed class WindowsScheduler(AppPaths paths, CatalogStore catalog, ProcessRunner processes, string? runnerOverride = null)
{
    public static string RunnerPath => Path.Combine(AppPaths.InstalledDirectory, "RepoBackup.Runner.exe");
    private string EffectiveRunnerPath => runnerOverride ?? RunnerPath;
    public static string BuildXml(ScheduleDefinition schedule, string runner, string dataDirectory, string userSid, DateTime? now = null)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var date = (now ?? DateTime.Now).Date.Add(schedule.Time.ToTimeSpan());
        XElement trigger;
        if (schedule.Kind == ScheduleKind.Interval)
        {
            if (schedule.IntervalHours is < 1 or > 8760) throw new ArgumentException("Interval must be between 1 and 8760 hours.");
            trigger = new(ns + "TimeTrigger", new XElement(ns + "Repetition", new XElement(ns + "Interval", $"PT{schedule.IntervalHours}H"), new XElement(ns + "StopAtDurationEnd", false)), new XElement(ns + "StartBoundary", date.ToString("yyyy-MM-ddTHH:mm:ss")), new XElement(ns + "Enabled", true));
        }
        else
        {
            var calendar = schedule.Kind == ScheduleKind.Daily ? new XElement(ns + "ScheduleByDay", new XElement(ns + "DaysInterval", 1))
                : new XElement(ns + "ScheduleByWeek", new XElement(ns + "WeeksInterval", 1), new XElement(ns + "DaysOfWeek", new XElement(ns + schedule.Day.ToString())));
            trigger = new(ns + "CalendarTrigger", new XElement(ns + "StartBoundary", date.ToString("yyyy-MM-ddTHH:mm:ss")), new XElement(ns + "Enabled", true), calendar);
        }
        var task = new XElement(ns + "Task", new XAttribute("version", "1.4"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "Repo Backup — " + schedule.Name)),
            new XElement(ns + "Triggers", trigger),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Author"), new XElement(ns + "UserId", userSid), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false), new XElement(ns + "StartWhenAvailable", true), new XElement(ns + "Enabled", schedule.Enabled), new XElement(ns + "Hidden", true), new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"), new XElement(ns + "Exec", new XElement(ns + "Command", runner), new XElement(ns + "Arguments", $"--data-dir \"{dataDirectory}\" run-schedule --id {schedule.Id}"), new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(runner)))));
        return new XDocument(new XDeclaration("1.0", "utf-16", null), task).ToString();
    }
    public async Task SaveAsync(ScheduleDefinition schedule, CancellationToken token = default)
    {
        if (!Guid.TryParse(schedule.Id, out _) || !Enum.IsDefined(schedule.Kind)) throw new ArgumentException("Invalid schedule identity or frequency.");
        if (!File.Exists(EffectiveRunnerPath)) throw new FileNotFoundException("Install the packaged app first. Scheduling requires its stable per-user runner path.", EffectiveRunnerPath);
        if (catalog.Destinations().All(d => d.Id != schedule.DestinationId)) throw new ArgumentException("Choose a saved destination.");
        if (schedule.SelectionId is not null && catalog.Selections().All(s => s.Id != schedule.SelectionId)) throw new ArgumentException("Saved selection no longer exists.");
        var xmlPath = Path.Combine(paths.DataDirectory, "schedule-" + schedule.Id + ".xml");
        await File.WriteAllTextAsync(xmlPath, BuildXml(schedule, EffectiveRunnerPath, paths.DataDirectory, WindowsIdentity.GetCurrent().User!.Value), System.Text.Encoding.Unicode, token);
        (await processes.RunAsync("schtasks.exe", ["/Create", "/TN", "RepoBackup-" + schedule.Id, "/XML", xmlPath, "/F"], token)).EnsureSuccess("Save Windows backup schedule");
        catalog.SaveSchedules(catalog.Schedules().Where(s => s.Id != schedule.Id).Append(schedule).ToList());
    }
    public Task DeleteAsync(string id, CancellationToken token = default)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Invalid schedule identity.");
        token.ThrowIfCancellationRequested();
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try
        {
            service.Connect(); folder = service.GetFolder("\\");
            try { ((dynamic)folder).DeleteTask("RepoBackup-" + id, 0); }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or FileNotFoundException && e.HResult == unchecked((int)0x80070002)) { }
        }
        finally
        {
            if (folder is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(service);
        }
        catalog.SaveSchedules(catalog.Schedules().Where(s => s.Id != id).ToList());
        return Task.CompletedTask;
    }
}
