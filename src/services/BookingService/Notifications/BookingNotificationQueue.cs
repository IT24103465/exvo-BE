using System.Threading.Channels;

namespace Exvo.BookingService.Notifications;

public sealed record TicketAttachment(string FileName, string ContentType, byte[] Content);

public interface IBookingNotificationQueue
{
    ValueTask QueueAsync(BookingNotification notification, CancellationToken cancellationToken = default);
    ValueTask<BookingNotification> DequeueAsync(CancellationToken cancellationToken);
}

public sealed record BookingNotification(int BookingId, IReadOnlyList<TicketAttachment> Tickets);

public sealed class BookingNotificationQueue : IBookingNotificationQueue
{
    private readonly Channel<BookingNotification> queue = Channel.CreateUnbounded<BookingNotification>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask QueueAsync(BookingNotification notification, CancellationToken cancellationToken = default) =>
        queue.Writer.WriteAsync(notification, cancellationToken);

    public ValueTask<BookingNotification> DequeueAsync(CancellationToken cancellationToken) =>
        queue.Reader.ReadAsync(cancellationToken);
}
