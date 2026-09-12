namespace ExvoAuthService.Models
{
    public record RegisterRequest(
        string FullName,
        string Email,
        string Password,
        string? Role, // "Attendee" or "Company"
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
        string Token,
        string Message,
        string? ProfilePicture = null,
        string? Address = null
    );

    public record ProfileResponse(
        int Id,
        int UserId,
        string Name,
        string Email,
        string? Address,
        string? PhoneNumber,
        string? ProfilePicture,
        string Role,
        string? CompanyName,
        string? CompanyRegNumber,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    public record UpdateProfileRequest(
        string Name,
        string Email,
        string? Address = null,
        string? PhoneNumber = null,
        string? ProfilePicture = null
    );
}
