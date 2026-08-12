using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace BeyondMovement.Api.Endpoints;

public static class ResultExtensions
{
    /// <summary>
    /// Turns a failed <see cref="Result"/> into RFC 7807 Problem Details carrying the stable
    /// errorCode and the request's correlationId (CLAUDE.md section 7). Provider messages and
    /// stack traces never reach the client.
    /// </summary>
    public static IResult ToProblem(this Error error, HttpContext http)
    {
        return Results.Problem(
            title: error.Message,
            statusCode: error.StatusCode,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = error.Code,
                ["correlationId"] = http.TraceIdentifier
            });
    }

    /// <summary>Validation failures use the same envelope, with per-field detail.</summary>
    public static IResult ToValidationProblem(
        this IDictionary<string, string[]> errors, HttpContext http)
    {
        return Results.ValidationProblem(
            errors,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = "VALIDATION_FAILED",
                ["correlationId"] = http.TraceIdentifier
            });
    }
}
