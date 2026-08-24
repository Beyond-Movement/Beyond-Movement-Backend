using BeyondMovement.Modules.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Scheduling.Persistence;

public interface ISchedulingDbContext
{
    DbSet<Session> Sessions { get; }
    DbSet<CalendlyWebhookEvent> CalendlyWebhookEvents { get; }
    DbSet<BookingOperation> BookingOperations { get; }
    DbSet<SchedulingChange> SchedulingChanges { get; }
    DbSet<CalendlyUnmatchedBooking> CalendlyUnmatchedBookings { get; }
    DbSet<CalendlyReconciliationRun> CalendlyReconciliationRuns { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
