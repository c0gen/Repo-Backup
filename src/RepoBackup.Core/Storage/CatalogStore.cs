using Microsoft.Data.Sqlite;
using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed partial class CatalogStore
{
    private readonly string connectionString;
    private readonly object writeGate = new();

    public CatalogStore(AppPaths paths, int timeoutSeconds = 30, bool recoverInterruptedJobs = true)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = timeoutSeconds
        }.ToString();
        InitializeSchema();
        if (recoverInterruptedJobs) RecoverInterruptedJobs();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static List<T> ReadAll<T>(SqliteConnection db, string table, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT payload FROM {table}";
        using var reader = command.ExecuteReader(); var items = new List<T>();
        while (reader.Read()) items.Add(Json.Read<T>(reader.GetString(0)));
        return items;
    }

    private List<T> ReadAll<T>(string table)
    {
        using var db = Open(); return ReadAll<T>(db, table);
    }

    private static void Upsert<T>(SqliteConnection db, SqliteTransaction? transaction, string table, string id, T value)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$payload", Json.Write(value)); command.ExecuteNonQuery();
    }

    private void Save<T>(string table, string id, T value)
    {
        using var db = Open(); Upsert(db, null, table, id, value);
    }

    public List<ProjectEntry> Projects() => ReadAll<ProjectEntry>("projects").OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public List<SavedSelection> Selections() => ReadAll<SavedSelection>("selections");
    public List<Destination> Destinations() => ReadAll<Destination>("destinations");
    public List<JobRecord> History() => ReadAll<JobRecord>("history").OrderByDescending(h => h.StartedAt).ToList();
    public List<ScheduleDefinition> Schedules() => GetSetting<List<ScheduleDefinition>>("schedules") ?? [];
    public void SaveSelection(SavedSelection value) => Save("selections", value.Id, value);
    public void SaveDestination(Destination value) => Save("destinations", value.Id, value);
    public void SaveJob(JobRecord value) => Save("history", value.Id, value);
    public void SaveSchedules(List<ScheduleDefinition> schedules) => SaveSetting("schedules", schedules);
    public void SaveSetting<T>(string key, T value) => Save("settings", key, value);

    private static T? ReadSetting<T>(SqliteConnection db, string key, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM settings WHERE id=$id"; command.Parameters.AddWithValue("$id", key);
        return command.ExecuteScalar() is string payload ? Json.Read<T>(payload) : default;
    }

    public T? GetSetting<T>(string key)
    {
        using var db = Open(); return ReadSetting<T>(db, key);
    }

    private void RecoverInterruptedJobs()
    {
        foreach (var job in History().Where(j => j.FinishedAt is null))
        {
            try
            {
                using var owner = System.Diagnostics.Process.GetProcessById(job.OwnerProcessId);
                if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == job.OwnerStartTicks) continue;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            SaveJob(job with
            {
                Backup = job.Backup == Outcome.Running ? Outcome.Interrupted : job.Backup,
                Verification = job.Verification == Outcome.Running ? Outcome.Interrupted : job.Verification,
                Cleanup = job.Cleanup == Outcome.Running ? Outcome.Interrupted : job.Cleanup,
                FinishedAt = DateTimeOffset.UtcNow,
                Messages = [.. job.Messages, "The owning process ended before the operation completed."]
            });
        }
    }
}
