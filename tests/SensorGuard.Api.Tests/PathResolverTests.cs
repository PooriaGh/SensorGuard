using System;
using System.IO;
using Shouldly;
using Xunit;

namespace SensorGuard.Api.Tests;

public sealed class PathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sensorguard-paths-" + Guid.NewGuid().ToString("N"));
    private readonly string _working;
    private readonly string _application;

    public PathResolverTests()
    {
        _working = Path.Combine(_root, "work");
        _application = Path.Combine(_root, "app");
        Directory.CreateDirectory(_working);
        Directory.CreateDirectory(_application);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void An_absolute_path_is_returned_unchanged_even_when_it_does_not_exist()
    {
        var absolute = Path.Combine(_root, "no-such-file.json");

        PathResolver.Resolve(absolute, _working, _application).ShouldBe(absolute);
    }

    [Fact]
    public void A_relative_path_that_exists_in_the_working_directory_wins_over_the_application_directory()
    {
        File.WriteAllText(Path.Combine(_working, "data.json"), "w");
        File.WriteAllText(Path.Combine(_application, "data.json"), "a");

        PathResolver.Resolve("data.json", _working, _application).ShouldBe("data.json");
    }

    [Fact]
    public void A_relative_path_missing_from_the_working_directory_falls_back_to_the_application_directory()
    {
        File.WriteAllText(Path.Combine(_application, "data.json"), "a");

        PathResolver.Resolve("data.json", _working, _application).ShouldBe(Path.Combine(_application, "data.json"));
    }

    [Fact]
    public void Subdirectories_are_supported_in_the_fallback()
    {
        Directory.CreateDirectory(Path.Combine(_application, "data"));
        File.WriteAllText(Path.Combine(_application, "data", "rules.json"), "a");

        PathResolver.Resolve(Path.Combine("data", "rules.json"), _working, _application)
            .ShouldBe(Path.Combine(_application, "data", "rules.json"));
    }

    [Fact]
    public void An_unknown_relative_path_is_returned_unchanged_so_the_reader_reports_it()
    {
        PathResolver.Resolve("definitely/not/here.json", _working, _application).ShouldBe("definitely/not/here.json");
    }
}
