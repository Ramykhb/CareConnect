using System.ComponentModel.DataAnnotations;

namespace CareConnect.Ai;

// The provider can extract fields, but only each application's server can validate and save a draft.
public sealed class AssistantState
{
    public string[] PetKeys { get; set; } = [];
    public int? NumberOfChildren { get; set; }
    public int? YoungestChildAgeYears { get; set; }
    public int? OldestChildAgeYears { get; set; }
    [StringLength(40)] public string? Service { get; set; }
    [StringLength(10)] public string? StartDate { get; set; }
    [StringLength(10)] public string? EndDate { get; set; }
    [StringLength(5)] public string? StartTime { get; set; }
    [StringLength(5)] public string? EndTime { get; set; }
    [StringLength(150)] public string? City { get; set; }
    [StringLength(300)] public string? StreetAddress { get; set; }
    [StringLength(4000)] public string? Instructions { get; set; }
    public decimal? Budget { get; set; }
    public bool? CompareOffers { get; set; }
}

public sealed record AssistantTurn(string Role, string Content, Guid? ClientMessageId = null);
public sealed record AssistantField(string Name, string Value);
public sealed record AssistantSnapshot(Guid ConversationId, IReadOnlyList<AssistantTurn> Messages,
    IReadOnlyList<AssistantField> Fields, string? DraftUrl, bool Available);
public sealed record AssistantContext(string Application, DateTimeOffset LocalNow, object Profile, string[] Services);
public sealed record AssistantExtraction(bool InScope, AssistantState State);

public interface IGroqDraftClient
{
    bool Available { get; }
    Task<AssistantExtraction> ExtractAsync(AssistantContext context, AssistantState state,
        IReadOnlyList<AssistantTurn> history, string message, CancellationToken ct);
}
public interface IRequestAssistantService
{
    Task<AssistantSnapshot> GetAsync(Guid userId, CancellationToken ct = default);
    Task<AssistantSnapshot> SendAsync(Guid userId, Guid conversationId, Guid clientMessageId, string message, CancellationToken ct = default);
    Task<AssistantSnapshot> ResetAsync(Guid userId, Guid conversationId, CancellationToken ct = default);
}
public sealed class AssistantException(string message) : Exception(message);

public static class AssistantJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string text) => System.Text.Json.JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new System.Text.Json.JsonException("Missing assistant state.");
}
