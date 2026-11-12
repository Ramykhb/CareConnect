using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public class PetCareRequestPet
{
    public Guid PetCareRequestId { get; set; }
    public PetCareRequest PetCareRequest { get; set; } = null!;
    public Guid PetId { get; set; }
    public Pet Pet { get; set; } = null!;
}
