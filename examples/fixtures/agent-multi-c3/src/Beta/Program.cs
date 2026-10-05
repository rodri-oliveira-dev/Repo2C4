var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/beta", () => Results.Ok());
app.Run();
