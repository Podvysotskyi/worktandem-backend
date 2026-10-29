using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Worktandem.Identity.Infrastructure;

namespace Worktandem.Identity.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOpenApi();
        builder.Services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("Identity"),
                postgres => postgres.SetPostgresVersion(18, 0)));
        builder.Services.AddHealthChecks().AddCheck<DatabaseReadiness>("database", tags: ["ready"]);

        var app = builder.Build();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        });
        // No startup DDL, Google callbacks, setup, session or business routes in this persistence slice.
        app.Run();
    }
}

internal sealed class DatabaseReadiness(IdentityDbContext database, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Identity")))
            return HealthCheckResult.Unhealthy();
        try
        {
            if (!await database.Database.CanConnectAsync(cancellationToken) ||
                (await database.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy();
            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            // Credentials and provider exception details must not enter the public probe response.
            return HealthCheckResult.Unhealthy();
        }
    }
}
