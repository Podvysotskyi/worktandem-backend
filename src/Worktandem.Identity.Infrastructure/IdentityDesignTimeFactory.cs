using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Worktandem.Identity.Infrastructure;

public sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        // Model/script generation needs no credentials or live server. Applying migrations requires an explicit connection.
        return new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Identity"),
                options => options.SetPostgresVersion(18, 0)).Options);
    }
}
