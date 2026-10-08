using System.Reflection;
using NUnit.Framework;
using Ticketing.Domain.Entities;

namespace Ticketing.Architecture.Tests;

/// <summary>Форма доменної моделі: сутностей 3–5, вони не анемічні й не мають публічних сеттерів.</summary>
[TestFixture]
public class DomainModelShapeTests
{
    private static IEnumerable<Type> Entities() =>
        typeof(Session).Assembly.GetTypes()
            .Where(t => t.Namespace == "Ticketing.Domain.Entities" && t.IsClass && t.IsPublic && !t.IsNested);

    [Test]
    public void Domain_has_the_expected_entities()
    {
        var names = string.Join(",", Entities().Select(t => t.Name).OrderBy(n => n));
        Assert.That(names, Is.EqualTo("Event,Order,Seat,Session,Ticket"));
    }

    [Test]
    public void Entities_are_not_anemic_each_has_behaviour_methods()
    {
        var anemic = Entities()
            .Where(t => !t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Any(m => !m.IsSpecialName))
            .Select(t => t.Name).ToList();
        Assert.That(anemic, Is.Empty, "Анемічні сутності: " + string.Join(", ", anemic));
    }

    [Test]
    public void Entities_have_no_public_setters()
    {
        var offenders = Entities()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => $"{t.Name}.{p.Name}")).ToList();
        Assert.That(offenders, Is.Empty, "Публічні сеттери: " + string.Join(", ", offenders));
    }
}
