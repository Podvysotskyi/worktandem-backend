using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
        var serviceExpected = path == "/health/ready" ? HttpStatusCode.ServiceUnavailable : expected;
        await AssertEndpoint<Organization.Api.Program>(environment, path, serviceExpected);
        await AssertEndpoint<Identity.Api.Program>(environment, path, serviceExpected);
        await AssertEndpoint<Gateway.Program>(environment, path, expected);
    }

    private static async Task AssertEndpoint<T>(string environment, string path, HttpStatusCode expected)
        where T : class
    {
        await using var factory = new WebApplicationFactory<T>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment)
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Identity"] = "", ["ConnectionStrings:Organization"] = "" })));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        if (path.StartsWith("/health/"))
            Assert.Equal(expected == HttpStatusCode.OK ? "Healthy" : "Unhealthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/setup/complete")]
    [InlineData("/auth/start")]
    [InlineData("/companies/applications")]
    [InlineData("/platform/applications/00000000-0000-0000-0000-000000000001/decision")]
    [InlineData("/platform/users/00000000-0000-0000-0000-000000000001/suspend")]
    [InlineData("/platform/users/00000000-0000-0000-0000-000000000001/reinstate")]
    [InlineData("/invitations/accept")]
    public async Task Feature_and_privileged_writes_remain_absent(string path)
    {
        await AssertNoWrite<Identity.Api.Program>(path, path is "/auth/start" or "/setup/complete" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
        await AssertNoWrite<Organization.Api.Program>(path);
        await AssertNoWrite<Gateway.Program>(path);
    }

    private static async Task AssertNoWrite<T>(string path, HttpStatusCode expected = HttpStatusCode.NotFound) where T : class
    {
        await using var factory = new WebApplicationFactory<T>();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(path, new StringContent("{}"));
        Assert.Equal(expected, response.StatusCode);
    }
}
