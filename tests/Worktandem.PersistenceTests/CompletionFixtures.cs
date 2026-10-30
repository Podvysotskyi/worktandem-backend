using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Worktandem.Identity.Application;
using Worktandem.Identity.Domain;
using Worktandem.Identity.Infrastructure;

namespace Worktandem.PersistenceTests;

internal sealed class FixtureSecret : IBootstrapSecretVerifier
{
    public const string Value = "fixture-only-bootstrap-secret-never-for-production";
    public bool Enabled { get; set; }
    public bool IsConfigured => Enabled;
    public bool Matches(string entered) => Enabled && SecretComparison.Matches(Value, entered);
}

internal sealed class FixtureAssurance : IPrivilegedAssuranceGate
{
    public bool IsAvailable { get; set; }
}

internal sealed class RollbackBootstrapInterceptor : SaveChangesInterceptor
{
    public bool Enabled { get; set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Enabled && eventData.Context!.ChangeTracker.Entries<AuthenticationAudit>()
            .Any(entry => entry.Entity.EventName == "bootstrap.completed"))
            throw new Microsoft.EntityFrameworkCore.DbUpdateException("Deliberate fixture transaction failure.");
        return ValueTask.FromResult(result);
    }
}

internal sealed class FixtureCertificates : IDisposable
{
    private readonly RSA rootKey = RSA.Create(2048);
    private readonly RSA leafKey = RSA.Create(2048);
    private readonly RSA otherKey = RSA.Create(2048);
    public X509Certificate2 Root { get; }
    public X509Certificate2 Organization { get; }
    public X509Certificate2 Other { get; }

    public FixtureCertificates()
    {
        var now = DateTimeOffset.UtcNow;
        var root = new CertificateRequest("CN=Worktandem isolated test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        Root = root.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
        Organization = Leaf("CN=Organization fixture", leafKey, now);
        Other = Leaf("CN=Wrong service fixture", otherKey, now);
    }

    private X509Certificate2 Leaf(string subject, RSA key, DateTimeOffset now)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, true));
        using var issued = request.Create(Root, now.AddHours(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    public void Dispose() { Organization.Dispose(); Other.Dispose(); Root.Dispose(); rootKey.Dispose(); leafKey.Dispose(); otherKey.Dispose(); }
}

// TestServer does not perform a real TLS handshake. Inject only its peer-certificate feature;
// the actual certificate authentication handler still validates chain/EKU/expiry and pin.
internal sealed class FixtureCertificateFeature(FixtureCertificates certificates) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            context.Connection.ClientCertificate = context.Request.Headers["X-Fixture-Certificate"].ToString() switch
            {
                "organization" => certificates.Organization,
                "other" => certificates.Other,
                _ => null
            };
            await continuation(context);
        });
        next(app);
    };
}
