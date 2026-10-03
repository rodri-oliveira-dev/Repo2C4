using Consolidation.Persistence;

namespace Consolidation.Api.Consolidated;

public sealed class ConsolidatedQuery(ConsolidationDbContext database)
{
    public static void Get()
    {
        ConsolidationDbContext.Read();
    }
}
