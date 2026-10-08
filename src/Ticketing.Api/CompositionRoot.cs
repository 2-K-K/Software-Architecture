using System.Text;
using Ticketing.Application.Ports;
using Ticketing.Application.UseCases;
using Ticketing.Domain.Repositories;
using Ticketing.Infrastructure.Persistence;
using Ticketing.Infrastructure.Services;

namespace Ticketing.Api;

/// <summary>
/// Єдина точка конфігурації впровадження залежностей. Лише тут інтерфейси зв'язуються з реалізаціями;
/// жоден інший клас не створює реалізації інших шарів самостійно.
/// </summary>
public static class CompositionRoot
{
    public static IServiceCollection AddTicketingCore(this IServiceCollection services, IConfiguration configuration)
    {
        // Infrastructure: реалізації інтерфейсів домену та портів застосунку
        services.AddSingleton<IEventRepository, InMemoryEventRepository>();
        services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
        services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
        services.AddSingleton<ITicketRepository, InMemoryTicketRepository>();
        services.AddSingleton<IUnitOfWork, InMemoryUnitOfWork>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();

        var key = Encoding.UTF8.GetBytes(configuration["Tickets:SigningKey"] ?? "dev-only-signing-key-change-me");
        services.AddSingleton<ITicketCodeGenerator>(_ => new HmacTicketCodeGenerator(key));
        services.AddTransient<DemoDataSeeder>();

        // Application: сценарії використання
        services.AddScoped<ReserveSeatsHandler>();
        services.AddScoped<PayOrderHandler>();
        services.AddScoped<ValidateTicketHandler>();

        return services;
    }

    public static async Task<DemoData> SeedDemoDataAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
    }
}
