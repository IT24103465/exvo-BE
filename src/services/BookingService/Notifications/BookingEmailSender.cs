using System.Net;
using System.Net.Mail;
using Exvo.BookingService.Models;
using Exvo.BookingService.Services;
using Microsoft.Extensions.Options;

namespace Exvo.BookingService.Notifications;

public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "tickets@exvo.local";
    public bool EnableSsl { get; set; } = true;
    public string PickupDirectory { get; set; } = "notifications-outbox";
}

public interface IBookingEmailSender
{
    Task SendConfirmationAsync(Booking booking, EventTicketSnapshot? eventSnapshot, IReadOnlyList<TicketAttachment> tickets, CancellationToken cancellationToken);
}

public sealed class BookingEmailSender(IOptions<EmailOptions> options, IWebHostEnvironment environment) : IBookingEmailSender
{
    private readonly EmailOptions options = options.Value;

    public async Task SendConfirmationAsync(Booking booking, EventTicketSnapshot? eventSnapshot, IReadOnlyList<TicketAttachment> tickets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(booking.AttendeeEmail))
        {
            throw new InvalidOperationException($"Booking {booking.Id} does not have an attendee email.");
        }

        using var message = CreateMessage(booking, eventSnapshot, tickets);
        using var client = CreateClient();
        await client.SendMailAsync(message, cancellationToken);
    }

    private MailMessage CreateMessage(Booking booking, EventTicketSnapshot? eventSnapshot, IReadOnlyList<TicketAttachment> tickets)
    {
        var eventTitle = string.IsNullOrWhiteSpace(eventSnapshot?.Title) ? "Event unavailable" : eventSnapshot.Title;
        var localDateTime = EventLocalDateTime(eventSnapshot);
        var ticketReferences = tickets.Count > 0
            ? string.Join(Environment.NewLine, tickets.Select(ticket => $"- {Path.GetFileNameWithoutExtension(ticket.FileName)}"))
            : $"- {booking.BookingReference}";
        var message = new MailMessage(options.From, booking.AttendeeEmail)
        {
            Subject = $"EXVO booking confirmed - {booking.BookingReference}",
            Body = $"""
                   Your booking is confirmed.

                   Event: {eventTitle}
                   Date: {localDateTime:yyyy-MM-dd}
                   Time: {localDateTime:HH:mm}
                   Ticket reference:
                   {ticketReferences}
                   Total: {booking.Currency} {booking.TotalAmount:N2}

                   Your ticket image{(tickets.Count == 1 ? " is" : "s are")} attached to this email.
                   """,
            IsBodyHtml = false
        };
        foreach (var ticket in tickets)
        {
            message.Attachments.Add(new Attachment(new MemoryStream(ticket.Content), ticket.FileName, ticket.ContentType));
        }
        return message;
    }

    private static DateTime EventLocalDateTime(EventTicketSnapshot? eventSnapshot)
    {
        if (eventSnapshot is null || eventSnapshot.EventDate == DateTime.MinValue) return DateTime.UtcNow;
        return eventSnapshot.EventDate;
    }

    private SmtpClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            var pickup = Path.IsPathRooted(options.PickupDirectory)
                ? options.PickupDirectory
                : Path.Combine(environment.ContentRootPath, options.PickupDirectory);
            Directory.CreateDirectory(pickup);
            return new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = pickup
            };
        }

        var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl
        };
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        }
        return client;
    }
}
