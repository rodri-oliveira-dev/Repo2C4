var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ConsolidationConsumer>();
builder.Services.AddScoped<ConsolidationProcessor>();
