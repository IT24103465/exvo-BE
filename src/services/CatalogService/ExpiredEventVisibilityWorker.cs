using Exvo.CatalogService.Data;

namespace Exvo.CatalogService;

public sealed class ExpiredEventVisibilityWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ExpiredEventVisibilityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                await EventSchedule.HideExpiredAsync(db, clock.GetUtcNow().UtcDateTime);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Failed to update expired event visibility.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
