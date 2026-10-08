using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Ticketing.Api;
using Ticketing.Application.UseCases;
using Ticketing.Domain.Repositories;

namespace Ticketing.Architecture.Tests;

/// <summary>Єдина точка конфігурації: усі абстракції зв'язані, сценарії створюються контейнером.</summary>
[TestFixture]
public class CompositionRootTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection()
            .AddTicketingCore(new ConfigurationBuilder().Build())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    [Test]
    public void All_use_case_handlers_are_resolvable()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<ReserveSeatsHandler>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<PayOrderHandler>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<ValidateTicketHandler>(), Is.Not.Null);
    }

    [Test]
    public void Repository_interfaces_are_bound_to_infrastructure_implementations()
    {
        using var provider = Build();
        var repo = provider.GetRequiredService<ISessionRepository>();
        Assert.That(repo.GetType().Namespace, Is.EqualTo("Ticketing.Infrastructure.Persistence"));
    }
}
