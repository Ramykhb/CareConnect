using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}
