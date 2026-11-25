using PetSitting.Services.Receipts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Bookings;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OwnerBookingsController(OwnerBookingService bookings, UserManager<ApplicationUser> users, PetSitting.Services.Requests.IOfferChoiceService offers) : Controller
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
        var model = await bookings.DetailsAsync(OwnerId, id); if (model is null) return NotFound();
        if (model.Request.Status == RequestStatus.Open && model.Request.CustomerChoosesSitter) model.Offers = await offers.OffersAsync(OwnerId, model.Request.Id);
        return View(model);
    }
    [HttpGet]
    public async Task<IActionResult> DetailUpdates(Guid id)
    {
        var model = await bookings.DetailsAsync(OwnerId, id); if (model is null) return NotFound();
        if (model.Request.Status == RequestStatus.Open && model.Request.CustomerChoosesSitter) model.Offers = await offers.OffersAsync(OwnerId, model.Request.Id);
        return PartialView("_DetailBody", model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, string? version, string? reason) => Finish(await bookings.CancelAsync(OwnerId, id, version, reason), id);
    // Starts a Stripe card payment: the browser confirms the returned client secret with Stripe Elements.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PayIntent(Guid id, Guid clientToken, string? version)
    {
        var result = await bookings.CreatePaymentIntentAsync(OwnerId, id, clientToken, version);
        return result.Succeeded
            ? Json(new { clientSecret = result.ClientSecret })
            : Json(new { error = result.Error ?? "The card payment could not be started." });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(Guid id, Guid clientToken, string? version, string? paymentIntentId = null)
    {
        var result = await bookings.PayAsync(OwnerId, id, clientToken, version, paymentIntentId);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        if (string.Equals(Request.Headers["X-Requested-With"], "fetch", StringComparison.OrdinalIgnoreCase))
            return Json(new { ok = result.Succeeded, message = result.Message, redirect = Url.Action(nameof(Details), new { id }) });
        return RedirectToAction(nameof(Details), new { id });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid id, int rating, string? comment) => Finish(await bookings.ReviewAsync(OwnerId, id, rating, comment), id);
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChooseOffer(Guid id, Guid offerId, string? version) => Finish(await offers.ChooseAsync(OwnerId, id, offerId, version), id);
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
        return File(ReceiptPdf.Create(model), "application/pdf", $"CareConnect-{model.Item.Reference}-receipt.pdf");
    }
}
