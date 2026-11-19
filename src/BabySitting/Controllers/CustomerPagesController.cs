using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Notifications;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

// The owner/parent menu's Find Sitters, Payments and Reviews pages.
[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomerPagesController(BabySittingDbContext db, UserManager<ApplicationUser> users, TimeProvider clock) : Controller
{
    private async Task<(ApplicationUser Account, int Unread)?> Me()
    {
        var account = await users.GetUserAsync(User);
        if (account is null) return null;
        return (account, await NotificationVisibility.ForUser(db, account.Id).CountAsync(n => n.ReadAtUtc == null));
    }

    [HttpGet("find-sitters")]
    public async Task<IActionResult> FindSitters(string? city, ServiceType? service, string? search)
    {
        if (await Me() is not { } me) return Challenge();
        city = string.IsNullOrWhiteSpace(city) ? me.Account.City : city.Trim();
        var term = search?.Trim();
        var query = db.BabysitterProfiles.AsNoTracking().Where(p => p.IsAvailable && p.User.IsActive && p.UserId != me.Account.Id);
        if (!string.IsNullOrWhiteSpace(city)) query = query.Where(p => p.City == city);
        if (service is { } s) query = query.Where(p => p.Services.Any(x => x.ServiceType == s));
        if (!string.IsNullOrEmpty(term)) query = query.Where(p => (p.User.FirstName + " " + p.User.LastName).Contains(term) || p.Bio.Contains(term));
        var rows = await query.Select(p => new
        {
            p.Id, p.User.FirstName, p.User.LastName, p.City, p.Bio, p.YearsOfExperience, Verified = p.VerificationStatus == VerificationStatus.Verified,
            p.HourlyRate, p.Currency, Services = p.Services.Select(x => x.ServiceType).ToList(), Extra = p.HasFirstAidTraining ? "First aid trained" : null,
            Ratings = db.Reviews.Where(r => r.Booking.SitterProfileId == p.Id && r.ReviewerId == r.Booking.CustomerId).Select(r => (double)r.Rating).ToList()
        }).Take(60).ToListAsync();
        var cards = rows.Select(r => new SitterCard(r.Id, r.FirstName + " " + r.LastName, r.City, r.Bio, r.YearsOfExperience, r.Verified, r.HourlyRate, r.Currency,
                r.Services, r.Ratings.Count == 0 ? null : Math.Round(r.Ratings.Average(), 1), r.Ratings.Count, r.Extra))
            .OrderByDescending(c => c.Verified).ThenByDescending(c => c.Rating ?? 0).ThenByDescending(c => c.YearsOfExperience).ToList();
        var cities = await db.BabysitterProfiles.AsNoTracking().Where(p => p.IsAvailable && p.User.IsActive).Select(p => p.City).Distinct().OrderBy(c => c).Take(50).ToListAsync();
        ViewData["Title"] = "Find sitters";
        return View(new FindSittersViewModel { Account = me.Account, Unread = me.Unread, Sitters = cards, City = city, Service = service, Search = term, Cities = cities });
    }

    [HttpGet("my-payments")]
    public async Task<IActionResult> Payments()
    {
        if (await Me() is not { } me) return Challenge();
        var payments = await db.Payments.AsNoTracking().Where(p => p.Booking.CustomerId == me.Account.Id)
            .OrderByDescending(p => p.PaidAtUtc ?? p.CreatedAtUtc).Take(200)
            .Select(p => new PaymentRow(p.Id, p.Booking.RequestId, p.Booking.Request.Title, p.Booking.SitterProfile.User.FirstName + " " + p.Booking.SitterProfile.User.LastName,
                p.Amount, p.Currency, p.Status, p.PaidAtUtc ?? p.CreatedAtUtc, p.Status == PaymentStatus.Succeeded)).ToListAsync();
        var year = DashboardViewModel.Local(clock.GetUtcNow()).Year;
        ViewData["Title"] = "Payments";
        return View(new CustomerPaymentsViewModel { Account = me.Account, Unread = me.Unread, Payments = payments,
            PaidThisYear = payments.Where(p => p.Status == PaymentStatus.Succeeded && p.Currency == "USD" && DashboardViewModel.Local(p.At).Year == year).Sum(p => p.Amount),
            Refunded = payments.Where(p => p.Status == PaymentStatus.Refunded && p.Currency == "USD").Sum(p => p.Amount) });
    }

    [HttpGet("my-reviews")]
    public async Task<IActionResult> Reviews()
    {
        if (await Me() is not { } me) return Challenge();
        var reviews = await db.Reviews.AsNoTracking().Where(r => r.ReviewerId == me.Account.Id && r.Booking.CustomerId == me.Account.Id)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new ReviewRow(r.Booking.RequestId, r.Booking.Request.Title, r.Booking.SitterProfile.User.FirstName + " " + r.Booking.SitterProfile.User.LastName,
                r.Rating, r.Comment, r.SitterReply, r.CreatedAtUtc)).ToListAsync();
        var awaiting = await db.BabysittingBookings.AsNoTracking().Where(b => b.CustomerId == me.Account.Id && b.Status == BookingStatus.Completed && b.Review == null)
            .OrderByDescending(b => b.CompletedAtUtc ?? b.EndsAtUtc)
            .Select(b => new AwaitingReview(b.RequestId, b.Request.Title, b.SitterProfile.User.FirstName + " " + b.SitterProfile.User.LastName, b.CompletedAtUtc ?? b.EndsAtUtc)).ToListAsync();
        ViewData["Title"] = "Reviews";
        return View(new CustomerReviewsViewModel { Account = me.Account, Unread = me.Unread, Reviews = reviews, Awaiting = awaiting });
    }
}
