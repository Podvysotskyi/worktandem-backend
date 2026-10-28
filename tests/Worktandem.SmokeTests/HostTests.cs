using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Worktandem.SmokeTests;

public class HostTests
{
    [Theory]
    [InlineData("Development", "/health/live", HttpStatusCode.OK)]
    [InlineData("Development", "/health/ready", HttpStatusCode.OK)]
    [InlineData("Development", "/openapi/v1.json", HttpStatusCode.OK)]
    [InlineData("Production", "/openapi/v1.json", HttpStatusCode.NotFound)]
    [InlineData("Production", "/health/live", HttpStatusCode.OK)]
    [InlineData("Production", "/health/ready", HttpStatusCode.OK)]
    [InlineData("Production", "/organizations", HttpStatusCode.NotFound)]
    public async Task Hosts_expose_only_scaffold_endpoints(string environment, string path, HttpStatusCode expected)
    {
        await AssertEndpoint<Organization.Api.Program>(environment, path, expected);
        await AssertEndpoint<Gateway.Program>(environment, path, expected);
    }

    private static async Task AssertEndpoint<T>(string environment, string path, HttpStatusCode expected)
        where T : class
    {
        await using var factory = new WebApplicationFactory<T>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        if (path.StartsWith("/health/"))
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
