using System.ComponentModel.DataAnnotations;
namespace BabySitting.Models;

public sealed class BookingUpdate : Entity
{
    public Guid BookingId { get; set; }
    public BabysittingBooking Booking { get; set; } = null!;
    [MaxLength(2000)] public string Message { get; set; } = "";
    public Guid ClientToken { get; set; }
    public bool IsIssue { get; set; }
    public bool HasPhoto { get; set; }
    public BookingUpdatePhoto? Photo { get; set; }
}

public sealed class BookingUpdatePhoto : Entity
{
    public Guid BookingUpdateId { get; set; }
    public BookingUpdate BookingUpdate { get; set; } = null!;
    [Required, MaxLength(5242880)] public byte[] Bytes { get; set; } = [];
    [Required, MaxLength(30)] public string ContentType { get; set; } = "";
}
