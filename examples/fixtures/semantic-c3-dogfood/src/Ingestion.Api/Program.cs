using Ingestion.Api.Values;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IngestValueHandler>();
var app = builder.Build();
app.MapPost("/values", (IngestValueHandler handler) => IngestValueHandler.Handle());
app.Run();
