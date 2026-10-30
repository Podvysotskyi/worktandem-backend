using System.Security.Cryptography;
using System.Text;
using Worktandem.Identity.Application;

namespace Worktandem.Identity.Infrastructure;

public sealed class EnvironmentBootstrapSecretVerifier : IBootstrapSecretVerifier
{
    // Deliberately environment-only. JSON/appsettings/browser configuration cannot supply bootstrap authority.
    public bool IsConfigured => SecretComparison.IsConfigured(Environment.GetEnvironmentVariable("Worktandem__Bootstrap__Secret"));
    public bool Matches(string entered) =>
        SecretComparison.Matches(Environment.GetEnvironmentVariable("Worktandem__Bootstrap__Secret"), entered);
}

public static class SecretComparison
{
    public static bool IsConfigured(string? value) => value is { Length: >= 32 and <= 4096 };
    public static bool Matches(string? configured, string entered)
    {
        if (!IsConfigured(configured) || entered is null || entered.Length > 4096) return false;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configured!));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(entered));
        try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
        finally { CryptographicOperations.ZeroMemory(expected); CryptographicOperations.ZeroMemory(actual); }
    }
}
