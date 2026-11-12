using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class SitterService : Entity
{
    public Guid SitterProfileId { get; set; }
    public PetSitterProfile SitterProfile { get; set; } = null!;
    public ServiceType ServiceType { get; set; }
}
