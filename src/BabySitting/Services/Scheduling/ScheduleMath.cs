using BabySitting.Models;

namespace BabySitting.Services.Scheduling;

public readonly record struct ScheduleInterval(DateTimeOffset Start, DateTimeOffset End)
{
    public double Hours => Math.Max(0, (End - Start).TotalHours);
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => Start < end && End > start;
}

public static class ScheduleMath
{
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Asia/Beirut");
    public static DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, Zone).DateTime);
    public static DateTimeOffset Utc(DateOnly date, TimeOnly time = default)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // Some Beirut DST changes skip midnight; a whole day begins at its first valid instant.
        if (Zone.IsInvalidTime(local) && time == TimeOnly.MinValue)
            while (Zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (Zone.IsInvalidTime(local)) throw new ArgumentException("That local time does not exist because of a daylight-saving change. Choose another time.");
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }
    public static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    public static IEnumerable<ScheduleInterval> Occurrences(SitterScheduleException item, DateTimeOffset from, DateTimeOffset to)
    {
        var first = item.RepeatYearly ? LocalDate(from).Year - 1 : item.StartsOn.Year;
        var last = item.RepeatYearly ? LocalDate(to).Year : item.StartsOn.Year;
        for (var year = Math.Max(first, item.StartsOn.Year); year <= last && year < 9999; year++)
        {
            var endYear = year + item.EndsOn.Year - item.StartsOn.Year;
            if (endYear > 9998 || item.StartsOn.Day > DateTime.DaysInMonth(year, item.StartsOn.Month) || item.EndsOn.Day > DateTime.DaysInMonth(endYear, item.EndsOn.Month)) continue;
            var startDate = new DateOnly(year, item.StartsOn.Month, item.StartsOn.Day);
            var endDate = new DateOnly(endYear, item.EndsOn.Month, item.EndsOn.Day);
            var start = OccurrenceUtc(startDate, item.AllDay ? default : item.StartTime);
            var end = item.AllDay ? Utc(endDate.AddDays(1)) : OccurrenceUtc(endDate, item.EndTime);
            if (start < to && end > from) yield return new(start < from ? from : start, end > to ? to : end);
        }
    }
    private static DateTimeOffset OccurrenceUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // A yearly rule may land in a future DST gap even when its original time was valid.
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return Utc(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }
    public static IReadOnlyList<ScheduleInterval> Available(IEnumerable<SitterAvailability> windows, IEnumerable<SitterScheduleException> exceptions, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return [];
        var slots = windows.ToArray();
        var rules = exceptions.SelectMany(e => Occurrences(e, from, to).Select(i => new { Item = e, Interval = i })).ToArray();
        var points = slots.SelectMany(s => new[] { s.StartsAtUtc, s.EndsAtUtc }).Concat(rules.SelectMany(r => new[] { r.Interval.Start, r.Interval.End })).Append(from).Append(to).Where(p => p >= from && p <= to).Distinct().Order().ToArray();
        var result = new List<ScheduleInterval>();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var start = points[i]; var end = points[i + 1];
            var rule = rules.Where(r => r.Interval.Start <= start && r.Interval.End >= end).OrderByDescending(r => r.Item.CreatedAtUtc).ThenByDescending(r => r.Item.Id).FirstOrDefault();
            var available = rule?.Item.IsAvailable ?? (slots.Length == 0 || slots.Any(s => s.StartsAtUtc <= start && s.EndsAtUtc >= end));
            if (!available) continue;
            if (result.Count > 0 && result[^1].End == start) result[^1] = new(result[^1].Start, end);
            else result.Add(new(start, end));
        }
        return result;
    }
    public static bool Covered(IEnumerable<SitterAvailability> windows, IEnumerable<SitterScheduleException> exceptions, DateTimeOffset from, DateTimeOffset to)
    {
        var result = Available(windows, exceptions, from, to);
        return result.Count == 1 && result[0].Start == from && result[0].End == to;
    }
    public static IReadOnlyList<ScheduleInterval> Unavailable(IEnumerable<ScheduleInterval> available, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<ScheduleInterval>(); var through = from;
        foreach (var slot in available.OrderBy(s => s.Start)) { if (slot.Start > through) result.Add(new(through, slot.Start)); through = slot.End; }
        if (through < to) result.Add(new(through, to));
        return result;
    }
}
