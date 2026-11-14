using ClinIQ.Application.Features.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace ClinIQ.Infrastructure.Notifications;
internal sealed class FeedbackNotificationWorker(IServiceScopeFactory scopes, IOptions<FeedbackNotificationOptions> options, TimeProvider clock, ILogger<FeedbackNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<IFeedbackDispatcher>().RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Feedback delivery remains pending after {ExceptionType}.", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
