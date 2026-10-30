using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Worktandem.Organization.Api;
using Worktandem.Organization.Application;
using Worktandem.Organization.Domain;

namespace Worktandem.PersistenceTests;

internal sealed class OrganizationTestHost(string connection, GoogleTestHost identity) : WebApplicationFactory<Program>
{
    public RollbackCompanyInterceptor Rollback { get; } = new();
    public CapturedLogs Logs { get; } = new();
    public bool Available { get; set; } = true;
    public bool AuthorityAvailable { get; set; } = true;
    public bool WrongCertificate { get; set; }
    private HttpClient? authorityHttp;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Organization"] = connection }));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.RemoveAll<IOrganizationAvailability>();
            services.AddSingleton<IOrganizationAvailability>(new FixtureAvailability(this));
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(identity.Clock);
            services.AddSingleton<IInterceptor>(Rollback);
            services.RemoveAll<IIdentityAuthority>();
            authorityHttp = new HttpClient(new CertificateForwarder(this) { InnerHandler = identity.Server.CreateHandler() })
            { BaseAddress = new Uri("https://localhost"), Timeout = TimeSpan.FromSeconds(5) };
            services.AddScoped<IIdentityAuthority>(_ => new IdentityAuthorityClient(authorityHttp, identity.Clock));
        });
    }
    private sealed class FixtureAvailability(OrganizationTestHost host) : IOrganizationAvailability
    { public bool IsConfigured => host.Available; }
    private sealed class CertificateForwarder(OrganizationTestHost host) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!host.AuthorityAvailable) throw new HttpRequestException("Deliberate isolated authority failure.");
            request.Headers.Add("X-Fixture-Certificate", host.WrongCertificate ? "other" : "organization");
            return base.SendAsync(request, ct);
        }
    }
    public HttpClient Browser(string credential) => CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true })
        .WithBrowserCredential(credential);
    public override async ValueTask DisposeAsync() { await base.DisposeAsync(); authorityHttp?.Dispose(); }
}

internal static class OrganizationFixtureExtensions
{
    public static HttpClient WithBrowserCredential(this HttpClient client, string credential)
    { client.DefaultRequestHeaders.Add("Cookie", "__Host-wt-browser=" + credential); return client; }
}

internal sealed class RollbackCompanyInterceptor : SaveChangesInterceptor
{
    public bool Enabled { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
        InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (Enabled && data.Context!.ChangeTracker.Entries<CompanyAudit>().Any())
            throw new Microsoft.EntityFrameworkCore.DbUpdateException("Deliberate isolated company rollback.");
        return ValueTask.FromResult(result);
    }
}
