namespace PetSitting.Models;

public enum BookingStatus { Pending, AwaitingPayment, Confirmed, InProgress, Completed, Cancelled, Rejected }
public enum RequestStatus { Open, Booked, Completed, Cancelled, Expired }
public enum OfferStatus { Pending, Accepted, Rejected, Withdrawn, Expired }
public enum PaymentStatus { Pending, Succeeded, Failed, Refunded }
public enum VerificationStatus { NotSubmitted, Pending, Verified, Rejected }
public enum ServiceType { PetSitting, DogWalking, Boarding, DropInVisit }

public static class Roles
{
    public const string Customer = "PetOwner";
    public const string Sitter = "PetSitter";
    public const string PlatformManager = "PlatformManager";
}
