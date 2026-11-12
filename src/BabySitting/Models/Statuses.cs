namespace BabySitting.Models;

public enum BookingStatus { Pending, AwaitingPayment, Confirmed, InProgress, Completed, Cancelled, Rejected }
public enum RequestStatus { Open, Booked, Completed, Cancelled, Expired }
public enum OfferStatus { Pending, Accepted, Rejected, Withdrawn, Expired }
public enum PaymentStatus { Pending, Succeeded, Failed, Refunded }
public enum VerificationStatus { NotSubmitted, Pending, Verified, Rejected }
public enum ServiceType { Babysitting, AfterSchoolCare, OvernightCare }

public static class Roles
{
    public const string Customer = "Parent";
    public const string Sitter = "Babysitter";
    public const string PlatformManager = "PlatformManager";
}
