namespace BeyondMovement.Modules.Scheduling.Calendly;

public interface ISchedulingJobScheduler
{
    void EnqueueWebhook(Guid webhookId);
    void EnqueueReconciliation();
}
