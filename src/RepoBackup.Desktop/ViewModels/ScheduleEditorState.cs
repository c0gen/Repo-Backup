using RepoBackup.Core.Models;
using RepoBackup.Desktop.Infrastructure;

namespace RepoBackup.Desktop.ViewModels;

public sealed class ScheduleEditorState : ObservableObject
{
    private bool preservingDraft;
    public void PreserveDraft(Action refresh)
    {
        preservingDraft = true;
        try { refresh(); } finally { preservingDraft = false; }
        Raise(nameof(DestinationId)); Raise(nameof(SelectionId));
    }
    public string? Id { get; private set; }
    public string Mode => Id is null ? "New schedule" : "Edit schedule";
    private string name = "Project backups";
    public string Name { get => name; set => Set(ref name, value); }
    private string? destinationId;
    public string? DestinationId { get => destinationId; set { if (!preservingDraft) Set(ref destinationId, value); } }
    private string? selectionId;
    public string? SelectionId { get => selectionId; set { if (!preservingDraft) Set(ref selectionId, value); } }
    private bool enabled;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    private ScheduleKind kind;
    public ScheduleKind Kind { get => kind; set { if (Set(ref kind, value)) { Raise(nameof(UsesDay)); Raise(nameof(UsesInterval)); } } }
    public bool UsesDay => Kind == ScheduleKind.Weekly;
    public bool UsesInterval => Kind == ScheduleKind.Interval;
    private string time = "18:00";
    public string Time { get => time; set => Set(ref time, value); }
    private DayOfWeek day = DayOfWeek.Friday;
    public DayOfWeek Day { get => day; set => Set(ref day, value); }
    private string hours = "24";
    public string Hours { get => hours; set => Set(ref hours, value); }
    public void New(string? destination) => Load(new ScheduleDefinition { DestinationId = destination ?? "" }, true);
    public void Load(ScheduleDefinition schedule, bool isNew = false)
    {
        Id = isNew ? null : schedule.Id; Raise(nameof(Id)); Raise(nameof(Mode));
        Name = schedule.Name; DestinationId = schedule.DestinationId; SelectionId = schedule.SelectionId;
        Enabled = schedule.Enabled; Kind = schedule.Kind; Time = schedule.Time.ToString("HH:mm"); Day = schedule.Day; Hours = schedule.IntervalHours.ToString();
    }
    public ScheduleDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a schedule name.");
        if (string.IsNullOrEmpty(DestinationId)) throw new ArgumentException("Choose a schedule destination.");
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Choose a schedule frequency.");
        if (!TimeOnly.TryParse(Time, out var start)) throw new ArgumentException("Enter a valid start time, such as 18:00.");
        var interval = 24;
        if (UsesInterval && (!int.TryParse(Hours, out interval) || interval is < 1 or > 8760)) throw new ArgumentException("Enter an interval between 1 and 8760 hours.");
        if (UsesDay && !Enum.IsDefined(Day)) throw new ArgumentException("Choose a weekly day.");
        return new ScheduleDefinition { Id = Id ?? Guid.NewGuid().ToString("N"), Name = Name.Trim(), DestinationId = DestinationId, SelectionId = SelectionId, Enabled = Enabled, Kind = Kind, Time = start, Day = UsesDay ? Day : DayOfWeek.Friday, IntervalHours = interval };
    }
    public void Saved(ScheduleDefinition schedule) { Id = schedule.Id; Raise(nameof(Id)); Raise(nameof(Mode)); }
}
