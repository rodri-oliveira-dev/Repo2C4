using Consolidation.Api.Consolidated;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<ConsolidatedQuery>();
var app = builder.Build();
app.MapGet("/consolidated", (ConsolidatedQuery query) => ConsolidatedQuery.Get());
app.Run();
