using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Worktandem.Identity.Infrastructure;
using Worktandem.Organization.Infrastructure;
using Xunit;

namespace Worktandem.PersistenceTests;

// Never accepts a developer connection string: every database belongs to this disposable container.
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18.6";
    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private string? containerName;
    private string connection = "";

    public async Task InitializeAsync()
    {
        // Keep the exact identity before creation, so failed/uncertain starts can still be cleaned up.
        containerName = "worktandem-pg-test-" + runId;
        try
        {
            await Docker("run", "--detach", "--rm", "--name", containerName, "--label", "io.worktandem.test-run=" + runId,
                "--env", "POSTGRES_PASSWORD", "--publish", "127.0.0.1::5432", Image);
            var mapping = (await Docker("port", containerName, "5432/tcp")).Trim();
            var port = int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            connection = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = port,
                Username = "postgres",
                Password = password,
                Database = "postgres",
                Timeout = 2,
                IncludeErrorDetail = false
            }.ConnectionString;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var db = new NpgsqlConnection(connection);
                    await db.OpenAsync();
                    await using var version = new NpgsqlCommand("SHOW server_version", db);
                    Assert.StartsWith("18.6", (string)(await version.ExecuteScalarAsync())!);
                    break;
                }
                catch (NpgsqlException) when (attempt < 40) { await Task.Delay(500); }
            }
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task<string> CreateDatabase()
    {
        var name = "test_" + Guid.NewGuid().ToString("N"); // generated identifier, never user input
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var create = new NpgsqlCommand("CREATE DATABASE " + name, db);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(connection) { Database = name }.ConnectionString;
    }

    public static IdentityDbContext Identity(string connectionString) =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString, o => o.SetPostgresVersion(18, 0)).Options);
    public static OrganizationDbContext Organization(string connectionString) =>
        new(new DbContextOptionsBuilder<OrganizationDbContext>().UseNpgsql(connectionString, o => o.SetPostgresVersion(18, 0)).Options);

    public async Task DisposeAsync()
    {
        if (containerName is null) return;
        // The creation command may have failed before creating anything. Look up only this exact name.
        var found = (await Docker("ps", "--all", "--filter", "name=^/" + containerName + "$", "--format", "{{.Names}}")).Trim();
        if (found.Length == 0) { containerName = null; return; }
        if (found != containerName) throw new InvalidOperationException("Unexpected container lookup result.");
        var owner = (await Docker("inspect", "--format", "{{ index .Config.Labels \"io.worktandem.test-run\" }}", containerName)).Trim();
        if (owner != runId) throw new InvalidOperationException("Refusing to remove a container not owned by this fixture.");
        await Docker("rm", "--force", containerName);
        containerName = null;
    }

    private async Task<string> Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["POSTGRES_PASSWORD"] = password; // inherited by docker, never in arguments/logs
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Docker failed: " + await error);
        return await output;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
