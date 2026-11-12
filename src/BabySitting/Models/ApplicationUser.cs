using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
namespace BabySitting.Models;

public class ApplicationUser : IdentityUser<Guid>
{
    [Required, MaxLength(100)] public string FirstName { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string LastName { get; set; } = string.Empty;
    [MaxLength(150)] public string? City { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
    public BabysitterProfile? SitterProfile { get; set; }
}
