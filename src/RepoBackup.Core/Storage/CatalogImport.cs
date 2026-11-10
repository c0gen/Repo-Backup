using RepoBackup.Core.Infrastructure;
using RepoBackup.Core.Models;

namespace RepoBackup.Core.Storage;

public sealed partial class CatalogStore
{
    public string Export() => Json.Write(new CatalogExport(SchemaVersion, Projects(), Selections(), Destinations(), History(),
        Schedules().Select(s => s with { Enabled = false }).ToList()));

    public void Import(string json)
    {
        var data = Json.Read<CatalogExport>(json);
        if (data.SchemaVersion is not (1 or 2 or 3 or SchemaVersion)) throw new InvalidDataException("Unsupported catalog schema. Existing catalog has been preserved.");
        if (data.Selections.Any(s => !Enum.IsDefined(s.PatternBase)) || data.SchemaVersion < 3 &&
            (data.Selections.Any(s => s.PatternBase != ExclusionPatternBase.SelectedSource) || data.Destinations.Any(d => d.IsRetired)))
            throw new InvalidDataException("Selection pattern bases and retired destinations require catalog version 3.");
        if (data.Projects.Select(p => p.Id).Distinct().Count() != data.Projects.Count) throw new InvalidDataException("Duplicate project identity in import.");
        foreach (var destination in data.Destinations) PathSafety.Normalize(destination.Path);
        lock (writeGate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
            var existing = ReadAll<ProjectEntry>(db, "projects", transaction);
            var incoming = data.Projects.Select(p => PrepareSavedProject(p, existing.FirstOrDefault(e => e.Id == p.Id))).ToList();
            var combined = existing.Where(p => !incoming.Any(i => i.Id == p.Id)).Concat(incoming).ToList();
            foreach (var value in data.Selections) Upsert(db, transaction, "selections", value.Id, value);
            foreach (var value in data.Destinations) Upsert(db, transaction, "destinations", value.Id, value);
            foreach (var value in data.History) Upsert(db, transaction, "history", value.Id, value);
            var schedules = (ReadSetting<List<ScheduleDefinition>>(db, "schedules", transaction) ?? [])
                .Concat(data.Schedules.Select(s => s with { Enabled = false })).DistinctBy(s => s.Id).ToList();
            Upsert(db, transaction, "settings", "schedules", schedules);
            ReconcileIdentities(db, transaction, combined, validateOwnership: true); transaction.Commit();
        }
    }
}
