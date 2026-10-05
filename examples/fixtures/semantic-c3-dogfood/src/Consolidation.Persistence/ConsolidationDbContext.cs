using Microsoft.EntityFrameworkCore;

namespace Consolidation.Persistence;

public sealed class ConsolidationDbContext(DbContextOptions<ConsolidationDbContext> options)
    : DbContext(options)
{
    public static void Read()
    {
    }

    public static void Save()
    {
    }
}
