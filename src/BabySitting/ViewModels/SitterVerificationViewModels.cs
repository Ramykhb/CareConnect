using System.ComponentModel.DataAnnotations;
using BabySitting.Models;
using BabySitting.Services.Portal;
namespace BabySitting.ViewModels;

public sealed class VerificationForm
{
    [EnumDataType(typeof(VerificationDocument)), Display(Name = "Document")] public VerificationDocument DocumentType { get; set; } = VerificationDocument.NationalId;
    [StringLength(500), Display(Name = "Note for the manager")] public string? Note { get; set; }
}

public sealed class SitterVerificationViewModel : SitterPortalPage
{
    public VerificationForm Form { get; set; } = new();
    public IReadOnlyList<VerificationItem> History { get; init; } = [];
    public VerificationStatus Status => Profile?.VerificationStatus ?? VerificationStatus.NotSubmitted;
}
