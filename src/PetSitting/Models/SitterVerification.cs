using System.ComponentModel.DataAnnotations;
namespace PetSitting.Models;

public enum VerificationDocument { NationalId, Passport, DrivingLicence, Certificate }

// Proof a sitter sends so a platform manager can verify them. Only one can wait for review at a time.
public sealed class SitterVerification : Entity
{
    public Guid SitterProfileId { get; set; }
    public PetSitterProfile SitterProfile { get; set; } = null!;
    public VerificationDocument DocumentType { get; set; }
    [Required, MaxLength(200)] public string FileName { get; set; } = "";
    [Required, MaxLength(30)] public string ContentType { get; set; } = "";
    [Required, MaxLength(5242880)] public byte[] Bytes { get; set; } = [];
    [MaxLength(500)] public string? Note { get; set; }
    public VerificationStatus Status { get; set; } = VerificationStatus.Pending;
    public Guid? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    [MaxLength(500)] public string? DecisionReason { get; set; }
}
