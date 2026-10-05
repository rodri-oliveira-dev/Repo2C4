var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<OutboxBackgroundService>();
builder.Services.AddSingleton<RabbitMqOutboxMessagePublisher>();
