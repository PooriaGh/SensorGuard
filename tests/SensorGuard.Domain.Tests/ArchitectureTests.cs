using System;
using System.IO;
using System.Linq;
using SensorGuard.Domain.Model;
using Shouldly;
using Xunit;

namespace SensorGuard.Domain.Tests;

/// <summary>Principle I: dependencies point inward only; Domain references nothing external.</summary>
public sealed class ArchitectureTests
{
    private static readonly string[] ForbiddenForDomain =
    {
        "SensorGuard.Application", "SensorGuard.Infrastructure", "SensorGuard.Api",
        "Microsoft.Data.Sqlite", "Microsoft.AspNetCore", "System.Text.Json",
        "Microsoft.Extensions.Logging", "Microsoft.Extensions.DependencyInjection",
    };

    [Fact]
    public void Domain_assembly_references_no_infrastructure_or_framework_packages()
    {
        var referenced = typeof(DeviceId).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        foreach (var forbidden in ForbiddenForDomain)
        {
            referenced.Where(n => n.StartsWith(forbidden, StringComparison.Ordinal))
                .ShouldBeEmpty($"Domain must not reference {forbidden}");
        }
    }

    [Fact]
    public void Domain_project_file_has_no_package_or_project_references()
    {
        var text = File.ReadAllText(ProjectFile("src/SensorGuard.Domain/SensorGuard.Domain.csproj"));
        text.ShouldNotContain("PackageReference");
        text.ShouldNotContain("ProjectReference");
    }

    [Fact]
    public void Application_project_does_not_reference_infrastructure_or_api()
    {
        var text = File.ReadAllText(ProjectFile("src/SensorGuard.Application/SensorGuard.Application.csproj"));
        text.ShouldNotContain("SensorGuard.Infrastructure");
        text.ShouldNotContain("SensorGuard.Api");
    }

    private static string ProjectFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SensorGuard.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("solution root not found");
        return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
