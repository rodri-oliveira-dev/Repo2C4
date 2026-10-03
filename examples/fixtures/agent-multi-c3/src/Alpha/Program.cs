var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/alpha", () => Results.Ok());
app.Run();
