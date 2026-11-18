using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Services.Portal;

namespace BabySitting.Controllers;

// Platform managers confirm sitter payouts once the money was sent outside the app.
[Authorize(Roles = Roles.PlatformManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PayoutsController(IPayoutReviewService payouts) : Controller
{
    // GET /Payouts?status=requested|paid|cancelled|all
    [HttpGet]
    public async Task<IActionResult> Index(string? status, CancellationToken ct)
    {
        PayoutStatus? filter = status == "all" ? null : Enum.TryParse<PayoutStatus>(status, true, out var s) ? s : PayoutStatus.Requested;
        ViewBag.Filter = filter?.ToString().ToLowerInvariant() ?? "all";
        return View(await payouts.ListAsync(filter, ct));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(Guid id, string? version, CancellationToken ct)
    {
        var result = await payouts.MarkPaidAsync(id, version, ct);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }
}
