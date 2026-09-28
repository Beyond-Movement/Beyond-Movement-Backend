using BeyondMovement.Api.Endpoints;
using BeyondMovement.Api.Scheduling;
using BeyondMovement.Infrastructure.Storage;
using Microsoft.AspNetCore.Diagnostics;

namespace BeyondMovement.Api.Middleware;

/// <summary>
/// Turns object storage being unreachable into <c>503 STORAGE_UNAVAILABLE</c> wherever it
/// happens — including a note list that could not sign its image URLs — rather than a 500. The
/// SDK's own message was already logged by the storage adapter and is never echoed.
/// </summary>
public sealed class StorageExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not StorageUnavailableException) return false;

        await SessionNoteAttachmentErrors.StorageUnavailable.ToProblem(context).ExecuteAsync(context);
        return true;
    }
}
