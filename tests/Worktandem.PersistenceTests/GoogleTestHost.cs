using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Worktandem.Identity.Api;

namespace Worktandem.PersistenceTests;

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now = now.Add(duration);
}

internal sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(Messages);
    public void Dispose() { }
    private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}

internal sealed class GoogleTestHost(string connection) : WebApplicationFactory<Program>
{
    public GoogleSimulator Provider { get; } = new();
    public TestClock Clock { get; } = new();
    public CapturedLogs Logs { get; } = new();
    public string? OidcFailure { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = connection,
            ["Worktandem:Google:ClientId"] = "fixture-client",
            ["Worktandem:Google:ClientSecret"] = "fixture-client-secret"
        }));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            // In-memory test-only key ring; no machine key store, certificate or deployment credential is created.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.RemoveAll<IGoogleAuthAvailability>();
            services.AddSingleton<IGoogleAuthAvailability>(new Available());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<OpenIdConnectOptions>(GoogleAuthConfigurationName, options =>
            {
                options.Configuration = Provider.Configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(Provider.Configuration);
                options.Backchannel = new HttpClient(Provider, disposeHandler: false);
                var failureHandler = options.Events.OnRemoteFailure;
                options.Events.OnRemoteFailure = async context =>
                {
                    OidcFailure = context.Failure?.GetType().Name + ": " + context.Failure?.Message;
                    await failureHandler(context);
                };
            });
        });
    }

    private const string GoogleAuthConfigurationName = "Google";
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Provider.Dispose();
    }
    private sealed class Available : IGoogleAuthAvailability { public bool IsConfigured => true; }

    public HttpClient Browser(bool cookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = cookies
    });
}
