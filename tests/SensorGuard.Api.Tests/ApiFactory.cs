using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SensorGuard.Api.Tests;

/// <summary>Hosts the real application against temp files: ingestion runs at startup exactly as in production.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sensorguard-api-" + Guid.NewGuid().ToString("N"));
    private readonly string _inputPath;
    private readonly string _rulesPath;

    public ApiFactory(string inputContent, string rulesContent, string? existingDatabasePath = null)
    {
        Directory.CreateDirectory(_dir);
        _inputPath = Path.Combine(_dir, "input.json");
        _rulesPath = Path.Combine(_dir, "rules.json");
        DatabasePath = existingDatabasePath ?? Path.Combine(_dir, "test.db");
        File.WriteAllText(_inputPath, inputContent);
        File.WriteAllText(_rulesPath, rulesContent);
    }

    public string DatabasePath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("SensorGuard:InputFilePath", _inputPath);
        builder.UseSetting("SensorGuard:RulesFilePath", _rulesPath);
        builder.UseSetting("SensorGuard:DatabasePath", DatabasePath);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the temp directory is disposable.
            }
        }
    }
}
