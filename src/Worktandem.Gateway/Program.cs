using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Worktandem.Gateway;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Services.AddHttpClient(BrowserProxy.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(BrowserProxy.Handler);
        builder.Services.AddSingleton<BrowserProxy>();
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
        
        // Do not expose arbitrary downstream paths, internal authority, probes or future writes.
        app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"],
            (HttpContext context, BrowserProxy proxy) => proxy.Forward(context));
        app.MapGet("/signin-google", (HttpContext context, BrowserProxy proxy) => proxy.Forward(context));
        app.Run();
    }
}
