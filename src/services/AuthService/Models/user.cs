namespace ExvoAuthService.Models
{
    public class User
    {
        public int Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = "Attendee"; // "Attendee" or "Organizer"
        public string? CompanyName { get; set; }
        public string? CompanyRegNumber { get; set; }
        public string? ContactNumber { get; set; }
        public string? Address { get; set; }
        public string? ProfilePicture { get; set; } // Base64 data URL
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}