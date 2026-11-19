using PetSitting.Models;

namespace PetSitting.ViewModels;

public abstract class CustomerPage : IPortalViewModel
{
    public ApplicationUser Account { get; init; } = new();
    public PetSitterProfile? Profile => null;
    public int Unread { get; init; }
    public int IncomingRequestCount => 0;
}
public sealed record SitterCard(Guid ProfileId, string Name, string City, string Bio, int YearsOfExperience, bool Verified, decimal HourlyRate, string Currency,
    IReadOnlyList<ServiceType> Services, double? Rating, int ReviewCount, string? Extra);
public sealed class FindSittersViewModel : CustomerPage
{
    public IReadOnlyList<SitterCard> Sitters { get; init; } = [];
    public string? City { get; init; }
    public ServiceType? Service { get; init; }
    public string? Search { get; init; }
    public IReadOnlyList<string> Cities { get; init; } = [];
}
public sealed record PaymentRow(Guid PaymentId, Guid RequestId, string Title, string SitterName, decimal Amount, string Currency, PaymentStatus Status, DateTimeOffset At, bool Receipt);
public sealed class CustomerPaymentsViewModel : CustomerPage
{
    public IReadOnlyList<PaymentRow> Payments { get; init; } = [];
    public decimal PaidThisYear { get; init; }
    public decimal Refunded { get; init; }
}
public sealed record ReviewRow(Guid RequestId, string Title, string SitterName, int Rating, string? Comment, string? Reply, DateTimeOffset At);
public sealed record AwaitingReview(Guid RequestId, string Title, string SitterName, DateTimeOffset CompletedAt);
public sealed class CustomerReviewsViewModel : CustomerPage
{
    public IReadOnlyList<ReviewRow> Reviews { get; init; } = [];
    public IReadOnlyList<AwaitingReview> Awaiting { get; init; } = [];
}
