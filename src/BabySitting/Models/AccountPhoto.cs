using System.ComponentModel.DataAnnotations;
namespace BabySitting.Models;

public sealed class AccountPhoto : Entity
{
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    [Required, MaxLength(2097152)] public byte[] Bytes { get; set; } = [];
    [Required, MaxLength(30)] public string ContentType { get; set; } = "";
}
