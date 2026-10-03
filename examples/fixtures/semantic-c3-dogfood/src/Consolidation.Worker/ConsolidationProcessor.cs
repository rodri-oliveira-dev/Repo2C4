using Consolidation.Persistence;

public sealed class ConsolidationProcessor(ConsolidationDbContext database)
{
    public static void Process()
    {
        ConsolidationDbContext.Save();
    }
}
