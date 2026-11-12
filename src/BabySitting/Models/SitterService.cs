using System.ComponentModel.DataAnnotations;

namespace BabySitting.Models;

public class SitterService : Entity
{
    public Guid SitterProfileId { get; set; }
    public BabysitterProfile SitterProfile { get; set; } = null!;
    public ServiceType ServiceType { get; set; }
}
