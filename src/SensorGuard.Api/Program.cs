using SensorGuard.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSensorGuard(builder.Configuration);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SensorGuard.Startup");
var report = await Startup.RunIngestionAsync(app.Services, logger, CancellationToken.None);
if (report is null)
{
    return 2;
}

await app.RunAsync();
return 0;

public partial class Program;
