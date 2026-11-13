using System.ComponentModel.DataAnnotations;
using BabySitting.Models;
using BabySitting.Services.Scheduling;

namespace BabySitting.ViewModels;

public sealed class SitterAvailabilityViewModel : IPortalViewModel
{
    public ApplicationUser Account { get; init; } = new();
    public BabysitterProfile? Profile { get; init; }
    public int Unread { get; init; }
    public int IncomingRequestCount { get; init; }
    public DateTimeOffset Now { get; init; }
    public DateOnly SelectedDate { get; init; }
    public string View { get; init; } = "month";
    public IReadOnlyList<CalendarEventViewModel> Events { get; init; } = [];
    public IReadOnlyList<BabysittingBooking> ActiveBookings { get; init; } = [];
    public AvailabilityEditForm Editor { get; set; } = new();
    public DateOnly PeriodStart => View == "week" ? ScheduleMath.Monday(SelectedDate) : new(SelectedDate.Year, SelectedDate.Month, 1);
    public DateOnly PeriodEnd => View == "week" ? PeriodStart.AddDays(7) : PeriodStart.AddMonths(1);
    public DateOnly GridStart => ScheduleMath.Monday(PeriodStart);
    public int MonthCellCount => ((PeriodEnd.DayNumber - GridStart.DayNumber + 6) / 7) * 7;
    public IEnumerable<DateOnly> MonthDays => Enumerable.Range(0, MonthCellCount).Select(i => GridStart.AddDays(i));
    public IEnumerable<DateOnly> WeekDays => Enumerable.Range(0, 7).Select(i => PeriodStart.AddDays(i));
    public IReadOnlyList<ScheduleInterval> Available(DateOnly date) => Profile is null || !Profile.IsAvailable ? [] : ScheduleMath.Available(Profile.Availability, Profile.ScheduleExceptions, ScheduleMath.Utc(date), ScheduleMath.Utc(date.AddDays(1)));
    public IEnumerable<CalendarEventViewModel> On(DateOnly date) => Events.Where(e => e.Start < ScheduleMath.Utc(date.AddDays(1)) && e.End > ScheduleMath.Utc(date)).OrderBy(e => e.Start);
    public double AvailableHours => WeekDays.Sum(d => Available(d).Sum(i => i.Hours));
    public double BookedHours => Events.Where(e => !e.IsRequest && e.State != "completed").Sum(e => Math.Max(0, ((e.End < ScheduleMath.Utc(PeriodEnd) ? e.End : ScheduleMath.Utc(PeriodEnd)) - (e.Start > ScheduleMath.Utc(PeriodStart) ? e.Start : ScheduleMath.Utc(PeriodStart))).TotalHours));
    public double Utilisation => AvailableHours <= 0 ? 0 : Math.Round(BookedHours / AvailableHours * 100);
    public string SelectedStatus => Available(SelectedDate).Sum(i => i.Hours) <= 0 ? "Currently unavailable" : Available(SelectedDate).Sum(i => i.Hours) >= (ScheduleMath.Utc(SelectedDate.AddDays(1)) - ScheduleMath.Utc(SelectedDate)).TotalHours ? "Available all day" : "Partially available";
}

public sealed record CalendarEventViewModel(Guid Id, string Title, string Owner, DateTimeOffset Start, DateTimeOffset End, string State, bool IsRequest = false);

public sealed class AvailabilityEditForm : IValidatableObject
{
    [Required, MaxLength(32)] public string Version { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool ApplyRange { get; set; }
    public bool AllDay { get; set; } = true;
    public TimeOnly StartTime { get; set; } = new(9, 0);
    public TimeOnly EndTime { get; set; } = new(17, 0);
    public bool Available { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
    public bool RepeatYearly { get; set; }
    public string View { get; set; } = "month";
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        var end = ApplyRange ? EndDate : StartDate;
        if (StartDate.Year < 1901 || end.Year > 2099 || end < StartDate || end.DayNumber - StartDate.DayNumber > 365)
            yield return new("Choose a date range of up to one year between 1901 and 2099.", [nameof(StartDate)]);
        if (!AllDay && end == StartDate && EndTime <= StartTime) yield return new("The end time must be after the start time.", [nameof(EndTime)]);
        if (RepeatYearly && ((StartDate.Month == 2 && StartDate.Day == 29) || (end.Month == 2 && end.Day == 29)))
            yield return new("Yearly ranges cannot start or end on February 29. Use a one-off range for that date.", [nameof(RepeatYearly)]);
    }
    public SitterScheduleException ToException(Guid profileId, DateTimeOffset now) => new() { SitterProfileId = profileId, StartsOn = StartDate, EndsOn = ApplyRange ? EndDate : StartDate, StartTime = StartTime, EndTime = EndTime, AllDay = AllDay, IsAvailable = Available, Reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim(), RepeatYearly = RepeatYearly, CreatedAtUtc = now };
}
public sealed record ScheduleWriteResult(bool Succeeded, string Message);
