using Microsoft.EntityFrameworkCore;

namespace Ingestion.Persistence;

public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options)
    : DbContext(options)
{
    public static void Save()
    {
    }
}
