using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Worktandem.Organization.Infrastructure;

public sealed class OrganizationDesignTimeFactory : IDesignTimeDbContextFactory<OrganizationDbContext>
{
    public OrganizationDbContext CreateDbContext(string[] args)
    {
        // Model/script generation needs no credentials or live server. Applying migrations requires an explicit connection.
        return new(new DbContextOptionsBuilder<OrganizationDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Organization"),
                options => options.SetPostgresVersion(18, 0)).Options);
    }
}
