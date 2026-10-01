namespace ExvoAuthService.Models
{
    public record RegisterRequest(
        string FullName,
        string Email,
        string Password,
        string? Role, // " Attendee\ or \Organizer\
 string? CompanyName = null,
 string? CompanyRegNumber = null,
 string? ContactNumber = null
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
    string? CompanyName,
    string? CompanyRegNumber,
    string? ContactNumber,
    string? ProfilePicture,
    string Token,
    string Message,
    string? Address = null
    );

    public record UpdateProfileRequest(
    string? FullName,
    string? Name,
    string? Email,
    string? ContactNumber,
    string? PhoneNumber,
    string? Address,
    string? ProfilePicture
    );
}
