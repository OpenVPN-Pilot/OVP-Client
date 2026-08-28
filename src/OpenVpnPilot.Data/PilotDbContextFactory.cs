using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenVpnPilot.Data;

/// <summary>
/// Creates a context for the design time tooling that generates migrations.
/// </summary>
/// <remarks>
/// The connection string here is never used at runtime. It exists only so the tooling can build the
/// model, which is why it points at a throwaway file.
/// </remarks>
public sealed class PilotDbContextFactory : IDesignTimeDbContextFactory<PilotDbContext>
{
    public PilotDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<PilotDbContext> builder = new();
        builder.UseSqlite("Data Source=design-time.db");
        return new PilotDbContext(builder.Options);
    }
}
