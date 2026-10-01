using SensorGuard.Api;
using SensorGuard.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSensorGuard(builder.Configuration);

var app = builder.Build();
app.MapAggregationEndpoints();
app.MapReadModelEndpoints();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SensorGuard.Startup");
var report = await Startup.RunIngestionAsync(app.Services, logger, CancellationToken.None);
if (report is null)
{
    return 2;
}

app.Services.GetRequiredService<StartupResult>().Report = report;

await app.RunAsync();
return 0;

public partial class Program;
