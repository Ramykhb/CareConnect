using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;

namespace CareConnect.Ai;

public static class DevelopmentAiEnvironment
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "GROQ_API_KEY", "Ai:ApiKey", "Ai:Enabled", "Ai:Model", "Ai:TimeoutSeconds", "Ai:MaxCandidates", "Ai:AssistantTimeoutSeconds",
        "STRIPE_SECRET_KEY", "STRIPE_PUBLISHABLE_KEY", "STRIPE_WEBHOOK_SECRET",
        "Stripe:SecretKey", "Stripe:PublishableKey", "Stripe:WebhookSecret"
    };

    // Local development convenience only. Never execute .env text or import unrelated credentials.
    public static void AddDevelopmentAiEnvironment(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment()) return;
        var directory = new DirectoryInfo(builder.Environment.ContentRootPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CareConnect.sln")))
            {
                var file = Path.Combine(directory.FullName, ".env");
                if (File.Exists(file)) AddValues(builder.Configuration, File.ReadLines(file));
                return;
            }
            directory = directory.Parent;
        }
    }

    public static void AddValues(ConfigurationManager configuration, IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator < 1) continue;
            var key = line[..separator].Trim().Replace("__", ":");
            if (!Allowed.Contains(key)) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var end = value.IndexOf(value[0], 1);
                if (end < 1) continue;
                var trailing = value[(end + 1)..].TrimStart();
                if (trailing.Length > 0 && !trailing.StartsWith('#')) continue;
                value = value[1..end];
            }
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }
            if (value.Length > 0) values[key] = value;
        }
        if (values.Count == 0) return;
        // Defaults < local .env < user secrets < process environment < command-line arguments.
        var index = 0;
        for (var i = 0; i < configuration.Sources.Count; i++)
            if (configuration.Sources[i] is JsonConfigurationSource source && Path.GetFileName(source.Path)?.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) == true)
                index = i + 1;
        configuration.Sources.Insert(index, new MemoryConfigurationSource { InitialData = values });
    }
}
