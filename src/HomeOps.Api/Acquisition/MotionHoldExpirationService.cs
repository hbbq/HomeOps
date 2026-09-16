namespace HomeOps.Api.Acquisition;

public sealed class MotionHoldExpirationService(
    MeasurementPersistenceService persistence,
    MotionHoldScheduleSignal scheduleSignal,
    TimeProvider timeProvider,
    ILogger<MotionHoldExpirationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextExpiry = await persistence.ExpireMotionHoldsAsync(stoppingToken);
                if (nextExpiry is null)
                {
                    await scheduleSignal.WaitAsync(stoppingToken);
                    continue;
                }

                var delay = nextExpiry.Value - timeProvider.GetUtcNow();
                if (delay <= TimeSpan.Zero)
                {
                    continue;
                }

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delayTask = Task.Delay(delay, timeProvider, waitCancellation.Token);
                var signalTask = scheduleSignal.WaitAsync(waitCancellation.Token).AsTask();
                await Task.WhenAny(delayTask, signalTask);
                await waitCancellation.CancelAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Motion hold expiration failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
            }
        }
    }
}
