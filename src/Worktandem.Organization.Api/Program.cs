using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Worktandem.Organization.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOpenApi();
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["ready"]);
        
        var app = builder.Build();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }
        
        // Liveness only asserts that this host can answer an HTTP request.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        // No external dependencies exist yet. Register real dependency checks when introduced.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        });
        
        // Intentionally no business routes until authentication and scoped authorization exist.
        app.Run();
    }
}
