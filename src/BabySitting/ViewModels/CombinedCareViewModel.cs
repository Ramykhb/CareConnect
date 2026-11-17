using System.ComponentModel.DataAnnotations;
using BabySitting.Models;
namespace BabySitting.ViewModels;
public sealed class CombinedCareViewModel
{
    public bool Enabled { get; set; }
    public Guid RequestId { get; set; }
    [StringLength(160)] public string? Code { get; set; }
    public string? NewCode { get; set; }
    public IReadOnlyList<CombinedCareItem> Items { get; set; } = [];
    public IReadOnlyList<CombinedCareChoice> Choices { get; set; } = [];
}
public sealed record CombinedCareChoice(Guid Id, string Title);
public sealed record CombinedCareItem(CombinedCareLink Link, string Title, string Status, string? BookingStatus, int PendingMessages);
