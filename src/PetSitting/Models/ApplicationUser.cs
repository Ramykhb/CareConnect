using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
namespace PetSitting.Models;

public class ApplicationUser : IdentityUser<Guid>
{
    [PersonalData, Required, MaxLength(100)] public string FirstName { get; set; } = string.Empty;
    [PersonalData, Required, MaxLength(100)] public string LastName { get; set; } = string.Empty;
    [PersonalData, MaxLength(150)] public string? City { get; set; }
    [PersonalData] public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
    public PetSitterProfile? SitterProfile { get; set; }
}
