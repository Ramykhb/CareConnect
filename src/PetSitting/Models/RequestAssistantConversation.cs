using System.ComponentModel.DataAnnotations;

namespace PetSitting.Models;

public sealed class RequestAssistantConversation : Entity
{
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; } = Guid.NewGuid();
    [Required, MaxLength(100000)] public string MessagesJson { get; set; } = "[]";
    [Required, MaxLength(20000)] public string StateJson { get; set; } = "{}";
    public Guid? DraftId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
