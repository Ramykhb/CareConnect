using System.Data;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using CareConnect.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Messaging;
using PetSitting.ViewModels;
namespace PetSitting.Controllers;

[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CombinedCareController(PetSittingDbContext db, UserManager<ApplicationUser> users, IOptions<BrokerOptions> broker, IDataProtectionProvider protection) : Controller
{
    private Guid CustomerId => Guid.Parse(users.GetUserId(User)!);

    [HttpGet]
    public async Task<IActionResult> Index(Guid? requestId, CancellationToken ct)
        => View(await LoadAsync(new() { RequestId = requestId ?? Guid.Empty }, ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(CombinedCareViewModel model, CancellationToken ct)
    {
        if (!broker.Value.Enabled) return BadRequest("Enable RabbitMQ to use combined care.");
        if (!ModelState.IsValid) return View("Index", await LoadAsync(model, ct));
        var token = Guid.NewGuid().ToString("N") + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var error = await WriteAsync(async () =>
        {
            var request = await db.PetCareRequests.SingleOrDefaultAsync(r => r.Id == model.RequestId && r.CustomerId == CustomerId, ct);
            if (request is null) return "Choose one of your care requests.";
            if (request.StartsAtUtc <= DateTimeOffset.UtcNow || request.Status is not (RequestStatus.Open or RequestStatus.Booked))
                return "Only an upcoming open or booked request can be linked.";
            var link = await db.CombinedCareLinks.SingleOrDefaultAsync(l => l.LocalRequestId == request.Id, ct);
            if (link is not null)
            {
                if (link.OriginPlatform != "PetSitting" || link.PeerRequestId is not null)
                    return "This request is already linked.";
                try { token = protection.CreateProtector("CombinedCare.Invitation.v1").Unprotect(link.ProtectedInvitationCode!); }
                catch (Exception ex) when (ex is CryptographicException or ArgumentNullException)
                { return "This invitation code cannot be recovered. Restore the application's data-protection keys."; }
                if (link.InvitationExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    link.InvitationExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24);
                    CombinedCareOutbox.Enqueue(db, link, request, null, "Invited");
                }
                return null;
            }
            var id = Guid.ParseExact(token[..32], "N");
            link = new CombinedCareLink { Id = id, LocalRequestId = request.Id, OriginPlatform = "PetSitting",
                InvitationHash = Hash(token), ProtectedInvitationCode = protection.CreateProtector("CombinedCare.Invitation.v1").Protect(token), InvitationExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24) };
            db.CombinedCareLinks.Add(link);
            request.CorrelationId = id;
            var booking = await db.PetBookings.Where(b => b.RequestId == request.Id).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefaultAsync(ct);
            CombinedCareOutbox.Enqueue(db, link, request, booking?.Status.ToString(), "Invited");
            return null;
        }, ct);
        if (error is not null) { ModelState.AddModelError("", error); return View("Index", await LoadAsync(model, ct)); }
        model.NewCode = token;
        return View("Index", await LoadAsync(model, ct));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Join(CombinedCareViewModel model, CancellationToken ct)
    {
        if (!broker.Value.Enabled) return BadRequest("Enable RabbitMQ to use combined care.");
        var code = model.Code?.Trim() ?? "";
        if (!ModelState.IsValid || code.Length != 97 || !Guid.TryParseExact(code[..Math.Min(32, code.Length)], "N", out var correlation))
        {
            ModelState.AddModelError("Code", "Paste the complete one-time code from BabySitting.");
            return View("Index", await LoadAsync(model, ct));
        }
        var error = await WriteAsync(async () =>
        {
            var link = await db.CombinedCareLinks.SingleOrDefaultAsync(l => l.Id == correlation && l.OriginPlatform == "BabySitting", ct);
            if (link is null) return "The invitation has not arrived yet, or this code is invalid. Wait a few seconds and try again.";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(link.InvitationHash), Encoding.ASCII.GetBytes(Hash(code))))
                return "The invitation code is invalid.";
            if (link.LocalRequestId == model.RequestId && model.RequestId != Guid.Empty) return null;
            if (link.LocalRequestId is not null || link.InvitationExpiresAtUtc <= DateTimeOffset.UtcNow)
                return "This invitation has already been used or has expired.";
            if (link.PeerRequestStatus is not ("Open" or "Booked")) return "The originating request is no longer available.";
            var request = await db.PetCareRequests.SingleOrDefaultAsync(r => r.Id == model.RequestId && r.CustomerId == CustomerId, ct);
            if (request is null) return "Choose one of your care requests.";
            if (request.StartsAtUtc <= DateTimeOffset.UtcNow || request.Status is not (RequestStatus.Open or RequestStatus.Booked))
                return "Only an upcoming open or booked request can be linked.";
            if (await db.CombinedCareLinks.AnyAsync(l => l.LocalRequestId == request.Id, ct)) return "This request is already linked.";
            link.LocalRequestId = request.Id;
            request.CorrelationId = link.Id;
            request.ExternalPlatform = "BabySitting";
            request.ExternalRequestId = link.PeerRequestId;
            var booking = await db.PetBookings.Where(b => b.RequestId == request.Id).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefaultAsync(ct);
            CombinedCareOutbox.Enqueue(db, link, request, booking?.Status.ToString(), "Joined");
            return null;
        }, ct);
        if (error is not null) { ModelState.AddModelError("", error); return View("Index", await LoadAsync(model, ct)); }
        TempData["Success"] = "Your requests are linked. Each service keeps its own booking and payment.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<string?> WriteAsync(Func<Task<string?>> operation, CancellationToken ct)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var error = await operation();
                if (error is not null) return error;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            });
        }
        catch (DbUpdateConcurrencyException) { return "This request changed. Refresh and try again."; }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        { return "This invitation or request has already been linked. Refresh to see its current state."; }
    }

    private async Task<CombinedCareViewModel> LoadAsync(CombinedCareViewModel model, CancellationToken ct)
    {
        model.Enabled = broker.Value.Enabled;
        var requests = await db.PetCareRequests.AsNoTracking().Where(r => r.CustomerId == CustomerId).OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct);
        var ids = requests.Select(r => r.Id).ToArray();
        var links = await db.CombinedCareLinks.AsNoTracking().Where(l => l.LocalRequestId != null && ids.Contains(l.LocalRequestId.Value)).ToListAsync(ct);
        var bookings = await db.PetBookings.AsNoTracking().Where(b => ids.Contains(b.RequestId)).ToListAsync(ct);
        var correlations = links.Select(l => l.Id).ToArray();
        var pending = await db.MessageOutboxEntries.AsNoTracking().Where(m => correlations.Contains(m.CorrelationId) && m.PublishedAtUtc == null)
            .GroupBy(m => m.CorrelationId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, ct);
        model.Items = links.Select(l => { var r = requests.Single(r => r.Id == l.LocalRequestId); return new CombinedCareItem(l, r.Title, r.Status.ToString(),
            bookings.Where(b => b.RequestId == r.Id).OrderByDescending(b => b.Status is not (BookingStatus.Cancelled or BookingStatus.Rejected)).ThenByDescending(b => b.CreatedAtUtc).FirstOrDefault()?.Status.ToString(),
            pending.GetValueOrDefault(l.Id)); }).ToArray();
        model.Choices = requests.Where(r => (r.Status is RequestStatus.Open or RequestStatus.Booked) && r.StartsAtUtc > DateTimeOffset.UtcNow && links.All(l => l.LocalRequestId != r.Id || l.OriginPlatform == "PetSitting" && l.PeerRequestId == null))
            .Select(r => new CombinedCareChoice(r.Id, r.Title + " · " + r.StartsAtUtc.ToString("u"))).ToArray();
        return model;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
