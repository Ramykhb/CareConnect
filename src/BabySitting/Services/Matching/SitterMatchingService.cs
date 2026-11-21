using System.Globalization;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace BabySitting.Services.Matching;

// What the matcher knows about a sitter. Only numbers and flags: never names, emails, addresses or notes.
public sealed record MatchCandidate(Guid Id, int Experience, decimal HourlyRate, bool Verified, double? Rating, int Reviews, int Completed, bool FirstAid = false, string Currency = "USD");
// What the matcher knows about the request.
public sealed record MatchRequest(string Service, double Hours, decimal? Budget, int? YoungestChildAge = null, int? OldestChildAge = null);
public sealed record SitterMatch(Guid Id, double Score, string Reason, bool FromAi);

public sealed class AiMatchingOptions
{
    public const string Section = "Ai";
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "openai/gpt-oss-20b";
    public string Endpoint { get; set; } = "https://api.groq.com/openai/v1/chat/completions";
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxCandidates { get; set; } = 10;
}

public interface ISitterMatchingService
{
    Task<IReadOnlyList<SitterMatch>> RankAsync(MatchRequest request, IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default, bool useAi = true);
}

// Ranks sitters with local rules, then (when switched on) asks Groq to re-order the best few and explain each pick.
// Any AI problem (switched off, no key, timeout, bad answer) falls back to the rule order with a visible fallback in the UI.
public sealed class SitterMatchingService(IHttpClientFactory http, IOptions<AiMatchingOptions> options, ILogger<SitterMatchingService> log) : ISitterMatchingService
{
    public const string HttpClientName = "ai-matching";

    public async Task<IReadOnlyList<SitterMatch>> RankAsync(MatchRequest request, IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default, bool useAi = true)
    {
        ct.ThrowIfCancellationRequested();
        var ruled = candidates.Select(c => new SitterMatch(c.Id, Score(request, c), RuleReason(request, c), false))
            .OrderByDescending(m => m.Score).ToList();
        var o = options.Value;
        if (!useAi || !o.Enabled || string.IsNullOrWhiteSpace(o.ApiKey) || ruled.Count < 2) return ruled;
        var top = ruled.Take(Math.Clamp(o.MaxCandidates, 2, 20)).ToList();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 30)));
            var picks = await AskAsync(o, request, top.Select(m => candidates.First(c => c.Id == m.Id)).ToList(), timeout.Token);
            if (picks.Count == 0) return ruled;
            var byId = top.ToDictionary(m => m.Id);
            var reranked = picks.Select(p => byId[p.Id] with { Reason = p.Reason, FromAi = true }).ToList();
            reranked.AddRange(ruled.Where(m => reranked.All(r => r.Id != m.Id)));     // anything the AI left out keeps its rule order
            return reranked;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogWarning("AI sitter matching unavailable ({Error}); using the rule order.", e.GetType().Name);
            return ruled;
        }
    }

    // Higher is better. Verified sitters, good reviews, experience and a price inside the budget count most.
    public static double Score(MatchRequest r, MatchCandidate c)
    {
        double score = 0;
        if (c.Verified) score += 30;
        if (c.Rating is { } rating && c.Reviews > 0) score += rating * 8 * Math.Min(1, c.Reviews / 3.0);
        score += Math.Min(20, c.Completed * 2);
        score += Math.Min(15, c.Experience * 1.5);
        if (c.Currency == "USD" && r.Budget is { } budget && budget > 0 && r.Hours > 0)
        {
            var cost = c.HourlyRate * (decimal)r.Hours;
            score += cost <= budget ? 15 : -Math.Min(25, (double)((cost - budget) / budget) * 40);
        }
        if (c.FirstAid) score += r.YoungestChildAge is < 3 ? 15 : 8;
        return Math.Round(score, 2);
    }

    public static string RuleReason(MatchRequest r, MatchCandidate c)
    {
        var parts = new List<string>();
        if (c.Verified) parts.Add("verified");
        if (c.Rating is { } rating && c.Reviews > 0) parts.Add(rating.ToString("0.0", CultureInfo.InvariantCulture) + "★ from " + c.Reviews + " review" + (c.Reviews == 1 ? "" : "s"));
        if (c.Completed > 0) parts.Add(c.Completed + " completed booking" + (c.Completed == 1 ? "" : "s"));
        if (c.FirstAid) parts.Add("first aid trained");
        if (c.Experience > 0) parts.Add(c.Experience + " yrs experience");
        if (c.Currency == "USD" && r.Budget is { } budget && budget > 0 && r.Hours > 0 && c.HourlyRate * (decimal)r.Hours <= budget) parts.Add("fits your budget");
        if (c.Currency != "USD") parts.Add("rate in " + c.Currency + "; compare currency before booking");
        return parts.Count == 0 ? "Available for your dates" : char.ToUpperInvariant(parts[0][0]) + string.Join(" · ", parts)[1..];
    }

    // The prompt the AI receives. Sitters are "s1", "s2"…; their real ids never leave the app.
    public static string BuildPrompt(MatchRequest r, IReadOnlyList<MatchCandidate> candidates)
    {
        var data = new JsonObject
        {
            ["service"] = r.Service, ["hours"] = Math.Round(r.Hours, 1), ["budgetUsd"] = r.Budget, ["budgetCurrency"] = "USD",
            ["youngestChildAge"] = r.YoungestChildAge, ["oldestChildAge"] = r.OldestChildAge,
            ["sitters"] = new JsonArray(candidates.Select((c, i) => (JsonNode)new JsonObject
            {
                ["id"] = "s" + (i + 1), ["verified"] = c.Verified, ["rating"] = c.Rating, ["reviews"] = c.Reviews, ["completedBookings"] = c.Completed,
                ["yearsExperience"] = c.Experience, ["currency"] = c.Currency, ["hourlyRate"] = c.HourlyRate, ["estimatedCost"] = Math.Round(c.HourlyRate * (decimal)r.Hours, 2), ["firstAid"] = c.FirstAid
            }).ToArray())
        };
        return "You help a customer of a babysitting app choose a sitter. Rank the sitters below from best to worst match for this request, " +
               "weighing trust (verified, reviews, completed bookings), experience, and price against the budget. " +
               "Compare affordability only when the sitter currency equals the USD budget currency. No exchange rates are provided; never treat other currencies as USD or claim they fit the budget. " +
               "Reply with a JSON object containing a matches array, best first, with every sitter exactly once. " +
               "Use one short factual reason (max 15 words, no names) per sitter. Do not invent qualifications or guarantees.\n\n" +
               data.ToJsonString();
    }

    private async Task<List<(Guid Id, string Reason)>> AskAsync(AiMatchingOptions o, MatchRequest r, List<MatchCandidate> top, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, o.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = o.Model, max_completion_tokens = 2048, reasoning_effort = "low",
                messages = new[] { new { role = "user", content = BuildPrompt(r, top) } },
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "sitter_matches", strict = true,
                        schema = new
                        {
                            type = "object", additionalProperties = false, required = new[] { "matches" },
                            properties = new
                            {
                                matches = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object", additionalProperties = false, required = new[] { "id", "reason" },
                                        properties = new
                                        {
                                            id = new { type = "string", @enum = Enumerable.Range(1, top.Count).Select(i => "s" + i).ToArray() },
                                            reason = new { type = "string" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            })
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        using var response = await http.CreateClient(HttpClientName).SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new JsonException("Empty answer.");
        var choice = body["choices"]?.AsArray().FirstOrDefault();
        if ((string?)choice?["finish_reason"] != "stop") throw new JsonException("Incomplete answer.");
        var content = (string?)choice?["message"]?["content"] ?? throw new JsonException("Missing content.");
        var answer = JsonNode.Parse(content)?["matches"]?.AsArray() ?? throw new JsonException("Missing matches.");
        var text = answer.ToJsonString();
        return ParseAnswer(text, top);
    }

    // Reads the AI's JSON array, keeping only known sitters (each once) and short, plain reasons.
    public static List<(Guid Id, string Reason)> ParseAnswer(string text, IReadOnlyList<MatchCandidate> top)
    {
        int start = text.IndexOf('['), end = text.LastIndexOf(']');
        if (start < 0 || end <= start) throw new JsonException("No JSON array in the answer.");
        var picks = new List<(Guid, string)>();
        foreach (var item in JsonNode.Parse(text[start..(end + 1)])!.AsArray())
        {
            var key = (string?)item?["id"];
            if (key is null || !key.StartsWith('s') || !int.TryParse(key[1..], out var n) || n < 1 || n > top.Count) continue;
            var id = top[n - 1].Id;
            if (picks.Any(p => p.Item1 == id)) continue;
            var reason = new string(((string?)item?["reason"] ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            if (reason.Length == 0) reason = "Good match for your request";
            picks.Add((id, reason.Length > 140 ? reason[..140].TrimEnd() + "…" : reason));
        }
        return picks;
    }
}
