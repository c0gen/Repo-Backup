namespace RepoBackup.Desktop.ViewModels;

public sealed partial class SnapshotBrowserViewModel
{
    public string[] CoverageOptions { get; } = ["All coverage", "Full projects", "Partial selections"];
    public string[] StatusOptions { get; } = ["All statuses", "Successful", "Incomplete / interrupted"];
    public RecoverySeriesOption? SelectedSeries { get => SeriesOptions.FirstOrDefault(x => x.Id == state.Series); set { if (publishing || value is null || state.Series == value.Id) return; state.Series = value.Id; FilterChanged(); } }
    public string CoverageFilter { get => state.Coverage; set { if (state.Coverage == value || value is null) return; state.Coverage = value; FilterChanged(); } }
    public string StatusFilter { get => state.Status; set { if (state.Status == value || value is null) return; state.Status = value; FilterChanged(); } }
    public DateTime? DateFrom { get => state.From; set { if (state.From == value) return; state.From = value; FilterChanged(); } }
    public DateTime? DateThrough { get => state.Through; set { if (state.Through == value) return; state.Through = value; FilterChanged(); } }
    private bool MatchesSnapshot(SnapshotItem item) =>
        (state.Series.Length == 0 || CanonicalSeries(item.Snapshot.TagValue("series") ?? item.Snapshot.Id) == state.Series)
        && (state.From is null || item.Snapshot.Time.LocalDateTime.Date >= state.From.Value.Date)
        && (state.Through is null || item.Snapshot.Time.LocalDateTime.Date <= state.Through.Value.Date)
        && (state.Coverage == "All coverage" || (state.Coverage == "Full projects") == (item.Snapshot.Coverage == RepoBackup.Core.Models.Coverage.FullProject))
        && (state.Status == "All statuses" || (state.Status == "Successful") == item.Snapshot.Successful);
    private void FilterChanged() { RestoreFilters(); ApplySummaries(); _ = LoadFilesAsync(); }
    private void RestoreFilters()
    {
        foreach (var property in new[] { nameof(SelectedSeries), nameof(CoverageFilter), nameof(StatusFilter), nameof(DateFrom), nameof(DateThrough), nameof(Search) }) Raise(property);
    }
}
