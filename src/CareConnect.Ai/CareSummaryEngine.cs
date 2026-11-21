using System.Security.Cryptography;
using System.Text;

namespace CareConnect.Ai;

public sealed class CareSummaryEngine(ICareSummaryClient client, CareSummaryCache cache)
{
    public async Task<CareSummaryResult> GenerateAsync(Guid customerId, Guid bookingId, CareSummaryInput input, CancellationToken ct)
    {
        var completed = input.Sources.Count(s => s.Kind == "task" && s.CompletedAt is not null);
        var pending = input.Sources.Count(s => s.Kind == "task" && s.CompletedAt is null);
        var issues = input.Sources.Count(s => s.IsIssue);
        CareSummaryResult Result(string status, string message, IReadOnlyList<CareSummarySentence>? sentences = null, bool limited = false) =>
            new(status, message, sentences ?? [], input.Sources.Take(80).ToArray(), input.AsOf, input.Date, completed, pending, issues, input.UndatedTasks, limited, input.Cutoff ?? input.AsOf);
        if (input.Sources.Count == 0) return Result("empty", "No care tasks or sitter updates were recorded for this period. This does not confirm that care did or did not happen.");
        if (input.Sources.Count > 80 || input.Sources.Sum(s => s.Text.Length) > 24000)
            return Result("too-many", "This period has too many records for one AI summary. Select a specific day, or review the original care records. Up to 80 records are shown below.", limited: true);
        if (!client.Available) return Result("unavailable", "AI summaries are not configured. The recorded care details are shown below.");
        // Exclude the generation clock so identical records reuse a result, but edits, status changes and new records invalidate it.
        var fingerprint = AssistantJson.Serialize(input with { AsOf = default, Cutoff = null });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        var key = customerId.ToString("N") + ":" + bookingId.ToString("N") + ":" + digest;
        return await cache.GetAsync(key, async () =>
        {
            try
            {
                var sentences = await client.SummarizeAsync(input, ct);
                // Check citations even when an alternative provider is substituted.
                var validated = GroqCareSummaryClient.Parse(AssistantJson.Serialize(new { sentences }), input.Sources);
                return Result("ready", "AI highlights from recorded care. Open the source records to check the details.", validated);
            }
            catch (AssistantException e) { return Result("unavailable", e.Message); }
            catch (System.Text.Json.JsonException) { return Result("unavailable", "The AI returned unsupported highlights. Review the recorded care details below, or try again."); }
        }, ct);
    }
}
