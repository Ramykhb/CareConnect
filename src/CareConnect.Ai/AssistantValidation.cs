using System.Globalization;

namespace CareConnect.Ai;

public static class AssistantValidation
{
    public static bool Date(string? value, out DateOnly result) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    public static bool Time(string? value, out TimeOnly result) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

    public static List<string> Questions(AssistantState state, string[] services, bool boarding, DateTimeOffset now, TimeZoneInfo zone)
    {
        var questions = new List<string>();
        if (!services.Contains(state.Service, StringComparer.Ordinal)) questions.Add("Which service do you need: " + string.Join(", ", services.Select(ServiceLabel)) + "?");
        var hasStart = Date(state.StartDate, out var startDate) && Time(state.StartTime, out _);
        var hasEnd = Date(state.EndDate, out var endDate) && Time(state.EndTime, out _);
        if (!hasStart) questions.Add("What start date and time should I use (Beirut time)?");
        if (!hasEnd) questions.Add("What end date and time, or duration, should I use?");
        if (hasStart && hasEnd)
        {
            Time(state.StartTime, out var startTime); Time(state.EndTime, out var endTime);
            var start = startDate.ToDateTime(startTime, DateTimeKind.Unspecified);
            var end = endDate.ToDateTime(endTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(start) || zone.IsInvalidTime(end)) questions.Add("That time is skipped by daylight saving. Which other time should I use?");
            else
            {
                var from = new DateTimeOffset(start, zone.GetUtcOffset(start));
                var to = new DateTimeOffset(end, zone.GetUtcOffset(end));
                if (from <= now) questions.Add("Care must start in the future. What new start date and time should I use?");
                if (to <= from) questions.Add("Care must end after it starts. What end date and time should I use?");
                if ((to - from).TotalDays > 14) questions.Add("A request can last at most 14 days. What shorter date range should I use?");
            }
        }
        if (string.IsNullOrWhiteSpace(state.City)) questions.Add("In which city is care needed?");
        if (!boarding && string.IsNullOrWhiteSpace(state.StreetAddress)) questions.Add("What street address should I put on the draft? Please leave out entry codes.");
        if (state.Budget is not (>= 1 and <= 99999)) questions.Add("What fixed total budget in USD would you like to offer (between $1 and $99,999)?");
        return questions;
    }

    public static string ServiceLabel(string? service) => service switch
    {
        "PetSitting" => "Overnight care", "DogWalking" => "Dog walking", "DropInVisit" => "Drop-in visits",
        "AfterSchoolCare" => "After-school care", "OvernightCare" => "Overnight care", _ => service ?? "Not selected"
    };

    public static List<AssistantField> Fields(AssistantState state)
    {
        var fields = new List<AssistantField>();
        if (state.Service is not null) fields.Add(new("Service", ServiceLabel(state.Service)));
        if (state.StartDate is not null || state.StartTime is not null) fields.Add(new("Starts (Beirut)", $"{state.StartDate} {state.StartTime}".Trim()));
        if (state.EndDate is not null || state.EndTime is not null) fields.Add(new("Ends (Beirut)", $"{state.EndDate} {state.EndTime}".Trim()));
        if (!string.IsNullOrWhiteSpace(state.City)) fields.Add(new("City", state.City));
        if (!string.IsNullOrWhiteSpace(state.StreetAddress)) fields.Add(new("Address", state.StreetAddress));
        if (state.Budget is { } budget) fields.Add(new("Total budget", budget.ToString("0.##", CultureInfo.InvariantCulture) + " USD"));
        if (!string.IsNullOrWhiteSpace(state.Instructions)) fields.Add(new("Instructions", state.Instructions));
        return fields;
    }
}
