using Exvo.BookingService.Data;
using Exvo.BookingService.Models;
using Exvo.BookingService.Services;
using Microsoft.EntityFrameworkCore;

namespace Exvo.BookingService.Notifications;

public sealed class BookingNotificationWorker(
    IBookingNotificationQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BookingNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var notification = await queue.DequeueAsync(stoppingToken);
                await ProcessBookingAsync(notification, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Booking notification processing failed.");
            }
        }
    }

    private async Task ProcessBookingAsync(BookingNotification notification, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IBookingEmailSender>();
        var catalog = scope.ServiceProvider.GetRequiredService<IEventInventoryCatalogClient>();

        var booking = await db.Bookings
            .Include(item => item.Items)
                .ThenInclude(item => item.Seat)
                    .ThenInclude(seat => seat!.SeatingSection)
            .SingleOrDefaultAsync(item => item.Id == notification.BookingId, cancellationToken);
        if (booking is null || booking.Status != BookingStatus.Confirmed)
        {
            logger.LogWarning("Skipping notification for unavailable or unconfirmed booking {BookingId}.", notification.BookingId);
            return;
        }

        var snapshot = await catalog.GetEventSnapshotAsync(booking.EventId, cancellationToken);
        await emailSender.SendConfirmationAsync(booking, snapshot, notification.Tickets, cancellationToken);
        logger.LogInformation("Sent confirmation email for booking {BookingReference} to {AttendeeEmail}.",
            booking.BookingReference, booking.AttendeeEmail);
    }
}
