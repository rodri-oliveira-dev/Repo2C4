using Ingestion.Persistence;
using Microsoft.Extensions.Hosting;

public sealed class OutboxBackgroundService(
    RabbitMqOutboxMessagePublisher publisher,
    IngestionDbContext database) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RabbitMqOutboxMessagePublisher.Publish();
        IngestionDbContext.Save();
        return Task.CompletedTask;
    }
}
