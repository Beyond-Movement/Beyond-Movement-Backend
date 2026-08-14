using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.SharedKernel;
using FluentValidation.Results;

namespace BeyondMovement.Api.Endpoints;

public static class ResultExtensions
{
    /// <summary>
    /// Turns a failed <see cref="Result"/> into <see cref="ApiProblemDetails"/> — RFC 7807 plus
    /// the stable errorCode and the request's correlationId (CLAUDE.md section 7).
    /// <para>
    /// The response is written from the declared type rather than the framework's built-in
    /// problem details, so what ships at runtime and what the contract advertises are the same
    /// object. Provider messages and stack traces never reach the client.
    /// </para>
    /// </summary>
    public static IResult ToProblem(this Error error, HttpContext http)
    {
        if (error.RetryAfterSeconds is { } retryAfter)
            http.Response.Headers.RetryAfter = retryAfter.ToString();

        return Results.Json(
            new ApiProblemDetails
            {
                Type = ProblemTypeFor(error.StatusCode),
                Title = error.Message,
                Status = error.StatusCode,
                ErrorCode = error.Code,
                CorrelationId = http.TraceIdentifier,
                RetryAfterSeconds = error.RetryAfterSeconds
            },
            statusCode: error.StatusCode,
            contentType: "application/problem+json");
    }

    /// <summary>Validation failures use the same envelope, with per-field detail.</summary>
    public static IResult ToValidationProblem(this ValidationResult validation, HttpContext http)
    {
        var errors = validation.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return Results.Json(
            new ApiProblemDetails
            {
                Type = ProblemTypeFor(StatusCodes.Status400BadRequest),
                Title = "One or more validation errors occurred.",
                Status = StatusCodes.Status400BadRequest,
                ErrorCode = ApiErrorCodes.ValidationFailed,
                CorrelationId = http.TraceIdentifier,
                Errors = errors
            },
            statusCode: StatusCodes.Status400BadRequest,
            contentType: "application/problem+json");
    }

    private static string ProblemTypeFor(int statusCode) => statusCode switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        423 => "https://tools.ietf.org/html/rfc4918#section-11.3",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.5"
    };
}
