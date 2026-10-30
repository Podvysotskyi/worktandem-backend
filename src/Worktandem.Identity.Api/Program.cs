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
        // Callback URLs contain one-time codes/state. Never emit framework request URLs or auth payload diagnostics.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.None);
        builder.Services.AddOpenApi();
        builder.Services.AddGoogleLogin(builder.Configuration);
        builder.Services.AddInternalAuthority();
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate));
        builder.Services.AddDbContext<IdentityDbContext>((services, options) =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("Identity"),
                postgres => postgres.SetPostgresVersion(18, 0))
                .AddInterceptors(services.GetServices<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>()));
        builder.Services.AddHealthChecks().AddCheck<DatabaseReadiness>("database", tags: ["ready"]);

        var app = builder.Build();
        app.UsePendingGoogleLogin();
        app.MapAccountSessions();
        app.MapInternalAuthority();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        });
        // No startup DDL, company business routes or public global-status writes.
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
