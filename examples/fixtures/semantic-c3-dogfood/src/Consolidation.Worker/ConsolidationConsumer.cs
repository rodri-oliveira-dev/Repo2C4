using Microsoft.Extensions.Hosting;

public sealed class ConsolidationConsumer(ConsolidationProcessor processor)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ConsolidationProcessor.Process();
        return Task.CompletedTask;
    }
}
