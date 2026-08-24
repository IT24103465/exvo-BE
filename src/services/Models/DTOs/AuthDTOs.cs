namespace ExvoAuthService.Models
{
    public record RegisterRequest(
        string FullName,
        string Email,
        string Password,
        string Role // "Attendee" or "Organizer"
    );

    public record LoginRequest(
        string Email,
        string Password
    );

    public record AuthResponse(
        int Id,
        string FullName,
        string Email,
        string Role,
        string Token,
        string Message
    );
}