using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PetSitting.Data;
using Xunit;

namespace PetSitting.Tests;

public sealed class FoundationTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/Home/Privacy")]
    [InlineData("/health/live")]
    public async Task Application_serves_foundation_routes_without_external_services(string path)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        if (path == "/") Assert.Contains("CareConnect", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Persistence_uses_its_own_sql_server_database()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PetSittingDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Equal("CareConnect_PetSitting", context.Database.GetDbConnection().Database);
    }
}
