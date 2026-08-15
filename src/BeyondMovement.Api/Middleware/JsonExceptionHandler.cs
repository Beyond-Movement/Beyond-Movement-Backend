using System.Text.Json;
using BeyondMovement.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace BeyondMovement.Api.Middleware;

/// <summary>
/// Turns a malformed request body into 400, not 500.
/// <para>
/// A body that fails to deserialise — an unknown enum name, a string where a number belongs —
/// throws before any validator runs, so without this the caller is told the server broke when
/// in fact their request did. The parser's message is not echoed back; it can name internal
/// types.
/// </para>
/// </summary>
public sealed class JsonExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Minimal APIs wrap a body-parsing failure in BadHttpRequestException, so the
        // JsonException is only visible on the inner exception.
        var isMalformedBody = exception is JsonException
            || exception.InnerException is JsonException
            || exception is BadHttpRequestException;

        if (!isMalformedBody)
            return false;

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ApiProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            Title = "The request body could not be read. Check the field types and any enum values.",
            Status = StatusCodes.Status400BadRequest,
            ErrorCode = ApiErrorCodes.ValidationFailed,
            CorrelationId = context.TraceIdentifier
        }, cancellationToken);

        return true;
    }
}
