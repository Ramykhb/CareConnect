using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Bookings;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ParentBookingsController(ParentBookingService bookings, UserManager<ApplicationUser> users) : Controller
{
    private Guid OwnerId => Guid.Parse(users.GetUserId(User)!);
    [HttpGet]
    public async Task<IActionResult> Index(string? status, string? search, Guid? petId, ServiceType? service, int days = 0, int page = 1) =>
        View(await bookings.ListAsync(OwnerId, status, search, petId, service, days, page));
    [HttpGet]
    public async Task<IActionResult> Updates(string? status, string? search, Guid? petId, ServiceType? service, int days = 0, int page = 1) =>
        PartialView("_List", await bookings.ListAsync(OwnerId, status, search, petId, service, days, page));
    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var model = await bookings.DetailsAsync(OwnerId, id); return model is null ? NotFound() : View(model);
    }
    [HttpGet]
    public async Task<IActionResult> DetailUpdates(Guid id)
    {
        var model = await bookings.DetailsAsync(OwnerId, id); return model is null ? NotFound() : PartialView("_DetailBody", model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, string? version, string? reason) => Finish(await bookings.CancelAsync(OwnerId, id, version, reason), id);
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(Guid id, Guid clientToken, string? version) => Finish(await bookings.PayAsync(OwnerId, id, clientToken, version), id);
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid id, int rating, string? comment) => Finish(await bookings.ReviewAsync(OwnerId, id, rating, comment), id);
    private IActionResult Finish(BookingWriteResult result, Guid id)
    {
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Details), new { id });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Again(Guid id)
    {
        var draftId = await bookings.AgainAsync(OwnerId, id);
        return draftId is null ? NotFound() : RedirectToAction("Create", "CareRequests", new { draftId });
    }
    [HttpGet]
    public async Task<IActionResult> Receipt(Guid id)
    {
        var model = await bookings.DetailsAsync(OwnerId, id);
        if (model is null) return NotFound();
        if (!model.HasReceipt) return BadRequest("A receipt is available after a successful payment.");
        Response.Headers.ContentDisposition = $"attachment; filename=\"CareConnect-{model.Item.Reference}-receipt.html\"";
        return View(model);
    }
}
