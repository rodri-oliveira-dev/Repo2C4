using Ingestion.Persistence;

namespace Ingestion.Api.Values;

public sealed class IngestValueHandler(
    IngestionDbContext database,
    RedisIdempotencyStore cache)
{
    public void Handle()
    {
        IngestionDbContext.Save();
        RedisIdempotencyStore.Store();
    }
}
