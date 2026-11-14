using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Repositories;
using BabySitting.Services.Bookings;
using BabySitting.Services.Scheduling;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

// My requests: a parent creates, views, edits and deletes childcare requests, and cancels a booking.
// GET actions display pages; POST actions change data and then redirect (Post/Redirect/Get).
[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CareRequestsController(ICareRequestRepository repository, IFamilyRepository families, IBookingCancellationService cancellations, IParentBookingService parentBookings,
    UserManager<ApplicationUser> userManager, TimeProvider clock) : Controller
{
    private Guid UserId => Guid.Parse(userManager.GetUserId(User)!);

    // GET /CareRequests?status=open
    [HttpGet]
    public async Task<IActionResult> Index(string? status)
    {
        var now = clock.GetUtcNow();
        var bookings = await repository.GetBookingsForParentAsync(UserId);
        var items = (await repository.GetAllForParentAsync(UserId))
            .Select(r => new MyRequestItem(r, bookings.FirstOrDefault(b => b.RequestId == r.Id), now)).ToList();
        return View(new MyRequestsViewModel { Items = items, Filter = MyRequestsViewModel.Filters.Contains(status) ? status! : "all" });
    }

    // GET /CareRequests/Details/{id}
    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var request = await repository.GetByIdAsync(id, UserId);
        if (request is null) return NotFound();
        var booking = (await repository.GetBookingsForParentAsync(UserId, id)).FirstOrDefault();
        ViewBag.CanEdit = ICareRequestRepository.IsEditable(request, clock.GetUtcNow());
        return View(new MyRequestItem(request, booking, clock.GetUtcNow()));
    }

    // GET /CareRequests/Create — the family profile supplies the children's details.
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var family = await families.GetForUserAsync(UserId);
        if (family is null)
        {
            TempData["Error"] = "Create your family profile first, so sitters know who they'll care for.";
            return RedirectToAction("Create", "Family");
        }
        var start = DashboardViewModel.Local(clock.GetUtcNow()).Date.AddDays(1).AddHours(18);
        return View(new CareRequestFormViewModel
        {
            Title = "Evening babysitting", City = family.City, NumberOfChildren = family.NumberOfChildren,
            YoungestChildAgeYears = family.YoungestChildAgeYears, OldestChildAgeYears = family.OldestChildAgeYears,
            StartsAtLocal = start, EndsAtLocal = start.AddHours(4)
        });
    }

    // POST /CareRequests/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CareRequestFormViewModel form)
    {
        var family = await families.GetForUserAsync(UserId);
        if (family is null) return RedirectToAction("Create", "Family");
        var times = ToUtc(form);
        if (!ModelState.IsValid || times is null) return View(form);
        var request = new ChildcareRequest { CustomerId = UserId, FamilyProfileId = family.Id, CorrelationId = Guid.NewGuid(), Status = RequestStatus.Open, Currency = "USD" };
        Copy(form, request, times.Value);
        await repository.AddAsync(request);
        TempData["Success"] = "Your request was sent to sitters in " + request.City + ".";
        return RedirectToAction(nameof(Details), new { id = request.Id });
    }

    // GET /CareRequests/Edit/{id}
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var request = await repository.GetByIdAsync(id, UserId);
        if (request is null) return NotFound();
        if (!ICareRequestRepository.IsEditable(request, clock.GetUtcNow()))
        {
            TempData["Error"] = "Only an open request that hasn't started can be edited.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return View(ToForm(request));
    }

    // POST /CareRequests/Edit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, CareRequestFormViewModel form)
    {
        var times = ToUtc(form);
        if (!ModelState.IsValid || times is null) return View(form);
        byte[] version;
        try { version = Convert.FromBase64String(form.Version ?? ""); } catch (FormatException) { return BadRequest(); }
        var result = await repository.UpdateAsync(UserId, id, version, r => Copy(form, r, times.Value), clock.GetUtcNow());
        switch (result)
        {
            case CareRequestWriteResult.NotFound: return NotFound();
            case CareRequestWriteResult.NotEditable: TempData["Error"] = "This request can no longer be edited (a sitter accepted it, or it has started)."; break;
            case CareRequestWriteResult.Conflict: TempData["Error"] = "This request changed while you were editing. Review the latest details and try again."; break;
            default: TempData["Success"] = "Your request was updated. Sitters see the new details."; break;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /CareRequests/Delete/{id} — confirmation page; never delete on a GET request.
    [HttpGet]
    public async Task<IActionResult> Delete(Guid id)
    {
        var request = await repository.GetByIdAsync(id, UserId);
        if (request is null) return NotFound();
        return View(new MyRequestItem(request, null, clock.GetUtcNow()));
    }

    // POST /CareRequests/Delete/{id}
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id)
    {
        switch (await repository.DeleteOrCancelAsync(UserId, id))
        {
            case CareRequestWriteResult.NotFound: return NotFound();
            case CareRequestWriteResult.Deleted: TempData["Success"] = "Your request was deleted."; return RedirectToAction(nameof(Index));
            case CareRequestWriteResult.Cancelled: TempData["Success"] = "Your request was cancelled. Sitters who responded keep it in their history."; break;
            case CareRequestWriteResult.NotEditable: TempData["Error"] = "A booked request is cancelled by cancelling its booking."; break;
            default: TempData["Error"] = "This request changed. Refresh and try again."; break;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /CareRequests/CancelBooking — cancels the accepted booking of one of the parent's requests.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelBooking(Guid requestId, Guid bookingId, string? reason, string? version)
    {
        var result = await cancellations.CancelAsync(UserId, bookingId, reason, version);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id = requestId });
    }

    // POST /CareRequests/Pay — demo payment for an accepted booking (no card details, no real charge).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(Guid requestId, Guid bookingId, Guid clientToken, string? version)
    {
        var result = await parentBookings.PayAsync(UserId, bookingId, clientToken, version);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id = requestId });
    }

    // POST /CareRequests/Review — rate the sitter once care is completed.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid requestId, Guid bookingId, int rating, string? comment)
    {
        var result = await parentBookings.ReviewAsync(UserId, bookingId, rating, comment);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id = requestId });
    }

    // Converts the Beirut wall-clock times to UTC; adds a form error for a time skipped by daylight saving.
    private (DateTimeOffset Start, DateTimeOffset End)? ToUtc(CareRequestFormViewModel form)
    {
        if (form.StartsAtLocal is not { } s || form.EndsAtLocal is not { } e) return null;
        try
        {
            return (ScheduleMath.Utc(DateOnly.FromDateTime(s), TimeOnly.FromDateTime(s)), ScheduleMath.Utc(DateOnly.FromDateTime(e), TimeOnly.FromDateTime(e)));
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(form.StartsAtLocal), ex.Message);
            return null;
        }
    }

    private static CareRequestFormViewModel ToForm(ChildcareRequest r) => new()
    {
        Version = Convert.ToBase64String(r.RowVersion), Title = r.Title, Description = r.Description, ServiceType = r.ServiceType,
        StartsAtLocal = DashboardViewModel.Local(r.StartsAtUtc).DateTime, EndsAtLocal = DashboardViewModel.Local(r.EndsAtUtc).DateTime,
        City = r.City, ServiceAddress = r.ServiceAddress, Budget = r.Budget, NumberOfChildren = r.NumberOfChildren,
        YoungestChildAgeYears = r.YoungestChildAgeYears, OldestChildAgeYears = r.OldestChildAgeYears, CareInstructions = r.CareInstructions
    };

    private static void Copy(CareRequestFormViewModel f, ChildcareRequest r, (DateTimeOffset Start, DateTimeOffset End) times)
    {
        r.Title = f.Title.Trim(); r.Description = f.Description.Trim(); r.ServiceType = f.ServiceType;
        r.StartsAtUtc = times.Start; r.EndsAtUtc = times.End; r.City = f.City.Trim();
        r.ServiceAddress = string.IsNullOrWhiteSpace(f.ServiceAddress) ? null : f.ServiceAddress.Trim();
        r.Budget = f.Budget; r.NumberOfChildren = f.NumberOfChildren;
        r.YoungestChildAgeYears = f.YoungestChildAgeYears; r.OldestChildAgeYears = f.OldestChildAgeYears;
        r.CareInstructions = string.IsNullOrWhiteSpace(f.CareInstructions) ? null : f.CareInstructions.Trim();
    }
}
