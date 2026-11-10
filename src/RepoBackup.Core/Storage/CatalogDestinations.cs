using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed partial class CatalogStore
{
    public void ReplaceDestination(Destination previous, Destination replacement)
    {
        if (previous.Id == replacement.Id || replacement.IsRetired || replacement.RepositoryId is null)
            throw new ArgumentException("A replacement repository requires a new active identity.");
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var current = ReadAll<Destination>(db, "destinations", transaction).Single(d => d.Id == previous.Id);
            if (current != previous || current.IsRetired) throw new InvalidOperationException("The destination changed while it was being opened. Retry opening it.");
            Upsert(db, transaction, "destinations", previous.Id, previous with { IsRetired = true });
            Upsert(db, transaction, "destinations", replacement.Id, replacement);
            var schedules = ReadSetting<List<ScheduleDefinition>>(db, "schedules", transaction) ?? [];
            Upsert(db, transaction, "settings", "schedules", schedules.Select(s => s.DestinationId == previous.Id ? s with { Enabled = false } : s).ToList());
            transaction.Commit();
        }
    }
}
