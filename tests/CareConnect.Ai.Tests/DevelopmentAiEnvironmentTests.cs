using CareConnect.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Xunit;

namespace CareConnect.Ai.Tests;

public sealed class DevelopmentAiEnvironmentTests
{
    [Fact]
    public void Reads_supported_AI_values_without_importing_other_secrets_or_executing_text()
    {
        using var configuration = new ConfigurationManager();
        DevelopmentAiEnvironment.AddValues(configuration,
        ["# comment", "GROQ_API_KEY='local-test-key' # comment", "export Ai__Enabled=true", "Ai__Model=custom-model # comment", "RABBITMQ_PASSWORD=unrelated", "ConnectionStrings__DefaultConnection=unrelated", "Ai__ApiKey='unterminated"]);
        Assert.Equal("local-test-key", configuration["GROQ_API_KEY"]);
        Assert.Equal("true", configuration["Ai:Enabled"]);
        Assert.Equal("custom-model", configuration["Ai:Model"]);
        Assert.Null(configuration["RABBITMQ_PASSWORD"]);
        Assert.Null(configuration["ConnectionStrings:DefaultConnection"]);
        Assert.Null(configuration["Ai:ApiKey"]);
    }

    [Fact]
    public void Environment_and_user_secrets_override_dotenv_and_dotenv_overrides_defaults()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Ai:Enabled"] = "true" });
        // Model the standard appsettings source position without reading any local secrets.
        configuration.Sources.Add(new JsonConfigurationSource { Path = "appsettings.json", Optional = true });
        configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["GROQ_API_KEY"] = "host-key", ["Ai:Model"] = "secret-model" });
        DevelopmentAiEnvironment.AddValues(configuration, ["GROQ_API_KEY=dotenv-key", "Ai__Model=dotenv-model", "Ai__Enabled=false"]);
        Assert.Equal("host-key", configuration["GROQ_API_KEY"]);
        Assert.Equal("secret-model", configuration["Ai:Model"]);
        Assert.Equal("false", configuration["Ai:Enabled"]);
    }

    [Fact]
    public void Development_finds_repository_env_when_started_from_a_project_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "careconnect-env-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "src", "SampleApp"); Directory.CreateDirectory(content);
        try
        {
            File.WriteAllText(Path.Combine(root, "CareConnect.sln"), "");
            File.WriteAllText(Path.Combine(root, ".env"), "GROQ_API_KEY=local-fixture-key\nAi__Enabled=true");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = content, EnvironmentName = "Development" });
            using var configuration = builder.Configuration;
            configuration.Sources.Clear();
            builder.AddDevelopmentAiEnvironment();
            Assert.Equal("local-fixture-key", configuration["GROQ_API_KEY"]);
            Assert.Equal("true", configuration["Ai:Enabled"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Production_does_not_read_dotenv()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        var before = builder.Configuration.Sources.Count;
        builder.AddDevelopmentAiEnvironment();
        Assert.Equal(before, builder.Configuration.Sources.Count);
    }
}
