namespace CareConnect.Ai;

// Anonymous source keys go to Groq; original-record URLs remain in the application.
public sealed record CareSummarySource(string Key, string Kind, string Text, DateTimeOffset RecordedAt,
    DateTimeOffset? DueAt, DateTimeOffset? CompletedAt, bool IsIssue, string? OriginalUrl = null);
public sealed record CareSummaryInput(string Application, string Service, string BookingStatus, DateOnly? Date,
    DateTimeOffset AsOf, IReadOnlyList<CareSummarySource> Sources, int UndatedTasks = 0, DateTimeOffset? Cutoff = null);
public sealed record CareSummarySentence(string Text, string[] SourceKeys);
public sealed record CareSummaryResult(string Status, string Message, IReadOnlyList<CareSummarySentence> Sentences,
    IReadOnlyList<CareSummarySource> Sources, DateTimeOffset GeneratedAt, DateOnly? Date,
    int CompletedTasks, int PendingTasks, int Issues, int UndatedTasks, bool Limited = false, DateTimeOffset? StatusAsOf = null);

public interface ICareSummaryClient
{
    bool Available { get; }
    Task<IReadOnlyList<CareSummarySentence>> SummarizeAsync(CareSummaryInput input, CancellationToken ct);
}
public interface ICareSummaryService
{
    Task<CareSummaryResult?> GenerateAsync(Guid customerId, Guid bookingId, DateOnly? date, CancellationToken ct = default);
}
