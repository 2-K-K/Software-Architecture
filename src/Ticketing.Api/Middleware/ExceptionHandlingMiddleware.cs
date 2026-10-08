using Ticketing.Application.Exceptions;
using Ticketing.Domain.Exceptions;

namespace Ticketing.Api.Middleware;

/// <summary>Перетворює помилки домену та застосунку на HTTP-відповіді; бізнес-правил не містить.</summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    public ExceptionHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try { await _next(context); }
        catch (NotFoundException ex) { await WriteAsync(context, StatusCodes.Status404NotFound, ex.Message); }
        catch (DomainException ex) { await WriteAsync(context, StatusCodes.Status409Conflict, ex.Message); }
    }

    private static Task WriteAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
