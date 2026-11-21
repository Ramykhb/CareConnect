using CareConnect.Ai;
using Xunit;

namespace CareConnect.Ai.Tests;

public sealed class AssistantValidationTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Beirut");
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
    private static AssistantState Complete => new() { Service = "DogWalking", StartDate = "2026-11-22", EndDate = "2026-11-22", StartTime = "14:00", EndTime = "15:00", City = "Beirut", StreetAddress = "Cedar Street", Budget = 50 };

    [Fact]
    public void Complete_fields_pass_but_optional_notes_are_not_required() => Assert.Empty(AssistantValidation.Questions(Complete, ["DogWalking"], false, Now, Zone));

    [Fact]
    public void Missing_fields_are_asked_for_and_boarding_does_not_require_customer_address()
    {
        var state = new AssistantState { Service = "Boarding" };
        var questions = AssistantValidation.Questions(state, ["Boarding"], true, Now, Zone);
        Assert.DoesNotContain(questions, q => q.Contains("address"));
        Assert.Contains(questions, q => q.Contains("budget"));
        Assert.Contains(questions, q => q.Contains("start date"));
        Assert.Contains(questions, q => q.Contains("city"));
    }

    [Theory]
    [InlineData("2026-11-20", "2026-11-20", "14:00", "15:00", "future")]
    [InlineData("2026-11-22", "2026-11-22", "15:00", "14:00", "after")]
    [InlineData("2026-11-22", "2026-12-10", "14:00", "15:00", "14 days")]
    [InlineData("tomorrow", "2026-11-22", "14:00", "15:00", "start date")]
    [InlineData("2026-11-22", "2026-11-22", "25:00", "15:00", "start date")]
    public void Invalid_ranges_never_pass(string start, string end, string from, string to, string expected)
    {
        var state = Complete; state.StartDate = start; state.EndDate = end; state.StartTime = from; state.EndTime = to;
        Assert.Contains(AssistantValidation.Questions(state, ["DogWalking"], false, Now, Zone), q => q.Contains(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(100000)]
    public void Fixed_budget_must_match_the_wizard(decimal amount)
    {
        var state = Complete; state.Budget = amount;
        Assert.Contains(AssistantValidation.Questions(state, ["DogWalking"], false, Now, Zone), q => q.Contains("budget"));
    }

    [Fact]
    public void Scope_does_not_expand_to_unknown_services()
    {
        var state = Complete; state.Service = "PayInvoice";
        Assert.Contains(AssistantValidation.Questions(state, ["DogWalking"], false, Now, Zone), q => q.Contains("Which service"));
    }

    [Fact]
    public void Paid_provider_calls_are_rate_limited_per_user()
    {
        using var throttle = new AssistantThrottle(TimeProvider.System); var id = Guid.NewGuid();
        for (var i = 0; i < 8; i++) throttle.Check(id);
        Assert.Throws<AssistantException>(() => throttle.Check(id));
        throttle.Check(Guid.NewGuid());
    }
}
