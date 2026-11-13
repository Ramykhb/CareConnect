using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class PetSitterProfile : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [Required, MaxLength(2000)] public string Bio { get; set; } = string.Empty;
    [Required, MaxLength(150)] public string City { get; set; } = string.Empty;
    [Range(0, 80)] public int YearsOfExperience { get; set; }
    [Range(typeof(decimal), "0", "99999999")] public decimal HourlyRate { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.NotSubmitted;
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public Guid ScheduleRevision { get; set; } = Guid.NewGuid();
    public ICollection<SitterScheduleException> ScheduleExceptions { get; set; } = new List<SitterScheduleException>();
    public bool IsAvailable { get; set; } = true;
    public ICollection<SitterService> Services { get; set; } = new List<SitterService>();
    public ICollection<SitterAvailability> Availability { get; set; } = new List<SitterAvailability>();
}
