using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
namespace PetSitting.Models;

public class ApplicationUser : IdentityUser<Guid>
{
    [PersonalData, Required, MaxLength(100)] public string FirstName { get; set; } = string.Empty;
    [PersonalData, Required, MaxLength(100)] public string LastName { get; set; } = string.Empty;
    [PersonalData, MaxLength(150)] public string? City { get; set; }
    [PersonalData, MaxLength(300)] public string? HomeAddress { get; set; }
    [PersonalData, MaxLength(10)] public string PreferredLanguage { get; set; } = "en";
    [PersonalData, MaxLength(200)] public string? EmergencyContact { get; set; }
    public Guid? ProfilePhotoVersion { get; set; }
    [PersonalData] public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
    public PetSitterProfile? SitterProfile { get; set; }
}
