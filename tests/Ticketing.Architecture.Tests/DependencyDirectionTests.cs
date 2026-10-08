using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ticketing.Application.UseCases;
using Ticketing.Domain.Entities;
using Ticketing.Infrastructure.Persistence;

namespace Ticketing.Architecture.Tests;

/// <summary>
/// Автоматична перевірка напрямку залежностей:
/// Presentation -> Application -> Domain; Infrastructure -> Application, Domain; Domain не залежить ні від чого.
/// Перевірка двоступенева: (1) на рівні збірок — на що фактично посилається скомпільований код;
/// (2) на рівні вихідного коду — які простори імен імпортують файли.
/// </summary>
[TestFixture]
public class DependencyDirectionTests
{
    private static readonly Regex UsingDirective =
        new(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?<ns>[A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline);

    private static bool IsBaseClassLibrary(string name) =>
        name == "mscorlib" || name == "netstandard" ||
        (name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("System.Data", StringComparison.Ordinal));

    private static List<string> NonBclReferences(Assembly assembly, params string[] allowed) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(n => !IsBaseClassLibrary(n) && !allowed.Contains(n)).ToList();

    private static List<string> ForbiddenImports(string project, params string[] forbiddenPrefixes)
    {
        var violations = new List<string>();
        foreach (var file in SolutionPaths.SourceFiles(project))
            foreach (Match m in UsingDirective.Matches(File.ReadAllText(file)))
            {
                var ns = m.Groups["ns"].Value;
                if (forbiddenPrefixes.Any(p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal)))
                    violations.Add($"{Path.GetRelativePath(SolutionPaths.Root, file)}: using {ns}");
            }
        return violations;
    }

    // ---- Рівень збірок ----

    [Test]
    public void Domain_assembly_references_only_the_base_class_library()
    {
        var offenders = NonBclReferences(typeof(Session).Assembly);
        Assert.That(offenders, Is.Empty, "Domain залежить від: " + string.Join(", ", offenders));
    }

    [Test]
    public void Application_assembly_references_only_Domain_and_the_base_class_library()
    {
        var offenders = NonBclReferences(typeof(ReserveSeatsHandler).Assembly, "Ticketing.Domain");
        Assert.That(offenders, Is.Empty, "Application залежить від: " + string.Join(", ", offenders));
    }

    [Test]
    public void Infrastructure_assembly_does_not_reference_Api()
    {
        var offenders = NonBclReferences(typeof(InMemoryUnitOfWork).Assembly, "Ticketing.Domain", "Ticketing.Application");
        Assert.That(offenders, Is.Empty, "Infrastructure залежить від: " + string.Join(", ", offenders));
    }

    // ---- Рівень вихідного коду ----

    [Test]
    public void Domain_sources_do_not_import_other_layers_frameworks_or_data_access()
    {
        var violations = ForbiddenImports("Ticketing.Domain",
            "Ticketing.Application", "Ticketing.Infrastructure", "Ticketing.Api",
            "Microsoft", "System.Data", "System.Net", "Npgsql", "Hangfire", "StackExchange", "Newtonsoft");
        Assert.That(violations, Is.Empty, "Заборонені імпорти в Domain:\n" + string.Join("\n", violations));
    }

    [Test]
    public void Application_sources_do_not_import_infrastructure_presentation_or_data_access()
    {
        var violations = ForbiddenImports("Ticketing.Application",
            "Ticketing.Infrastructure", "Ticketing.Api",
            "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "System.Data", "Npgsql", "Hangfire", "StackExchange");
        Assert.That(violations, Is.Empty, "Заборонені імпорти в Application:\n" + string.Join("\n", violations));
    }

    [Test]
    public void Infrastructure_sources_do_not_import_presentation()
    {
        var violations = ForbiddenImports("Ticketing.Infrastructure", "Ticketing.Api", "Microsoft.AspNetCore");
        Assert.That(violations, Is.Empty, "Заборонені імпорти в Infrastructure:\n" + string.Join("\n", violations));
    }

    [Test]
    public void Presentation_uses_infrastructure_only_in_the_composition_root()
    {
        var violations = new List<string>();
        foreach (var file in SolutionPaths.SourceFiles("Ticketing.Api").Where(f => Path.GetFileName(f) != "CompositionRoot.cs"))
            foreach (Match m in UsingDirective.Matches(File.ReadAllText(file)))
                if (m.Groups["ns"].Value.StartsWith("Ticketing.Infrastructure", StringComparison.Ordinal))
                    violations.Add($"{Path.GetRelativePath(SolutionPaths.Root, file)}: using {m.Groups["ns"].Value}");
        Assert.That(violations, Is.Empty, "Presentation звертається до Infrastructure поза CompositionRoot:\n" + string.Join("\n", violations));
    }
}
