using System.ComponentModel.DataAnnotations;
using PetSitting.Models;
namespace PetSitting.ViewModels;

public sealed class OwnerProfileForm
{
    [Required, StringLength(100), RegularExpression(@".*\S.*"), Display(Name = "First name")]
    public string FirstName { get; set; } = "";
    [Required, StringLength(100), RegularExpression(@".*\S.*"), Display(Name = "Last name")]
    public string LastName { get; set; } = "";
    [Phone, StringLength(30), Display(Name = "Phone number")] public string? Phone { get; set; }
    [StringLength(150)] public string? City { get; set; }
    [StringLength(300), Display(Name = "Home address")] public string? HomeAddress { get; set; }
    [Required, RegularExpression("^(en|ar|fr)$"), Display(Name = "Language preference")]
    public string Language { get; set; } = "en";
    [StringLength(200), Display(Name = "Emergency contact")] public string? EmergencyContact { get; set; }
    [Required] public string Version { get; set; } = "";
}
public sealed class OwnerPasswordForm
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 12), DataType(DataType.Password), Display(Name = "New password")] public string NewPassword { get; set; } = "";
    [Required, Compare(nameof(NewPassword)), DataType(DataType.Password), Display(Name = "Confirm new password")] public string ConfirmPassword { get; set; } = "";
}
public sealed class OwnerSettingsViewModel
{
    public ApplicationUser Account { get; init; } = null!;
    public OwnerProfileForm Profile { get; set; } = new();
    public OwnerPasswordForm PasswordChange { get; set; } = new();
    public AlertPreferencesForm Alerts { get; set; } = new();
    public bool TwoFactorEnabled { get; init; }
    public bool HasPassword { get; init; }
    public DateTimeOffset? SignedInAt { get; set; }
}
