using Ticketing.Api;
using Ticketing.Api.Endpoints;
using Ticketing.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTicketingCore(builder.Configuration);

var app = builder.Build();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.MapTicketingEndpoints();

var demo = await app.Services.SeedDemoDataAsync();
app.Logger.LogInformation("Демонстраційний сеанс: {SessionId}", demo.SessionId);

// Демонстраційний ендпоінт: показує ідентифікатори тестових даних, щоб їх можна було підставити у запити.
app.MapGet("/api/demo", () => new { sessionId = demo.SessionId, eventId = demo.EventId });

app.Run();

public partial class Program;
