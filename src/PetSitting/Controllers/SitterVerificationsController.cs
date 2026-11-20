using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Portal;

namespace PetSitting.Controllers;

// Platform managers look at a sitter's proof and approve or reject it.
[Authorize(Roles = Roles.PlatformManager)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SitterVerificationsController(ISitterVerificationService verification, UserManager<ApplicationUser> users) : Controller
{
    private Guid ManagerId => Guid.Parse(users.GetUserId(User)!);

    // GET /SitterVerifications?status=pending|verified|rejected|all
    [HttpGet]
    public async Task<IActionResult> Index(string? status, CancellationToken ct)
    {
        VerificationStatus? filter = status == "all" ? null
            : Enum.TryParse<VerificationStatus>(status, true, out var s) && s != VerificationStatus.NotSubmitted ? s : VerificationStatus.Pending;
        ViewBag.Filter = filter?.ToString().ToLowerInvariant() ?? "all";
        return View(await verification.ListAsync(filter, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Document(Guid id, CancellationToken ct)
    {
        var file = await verification.FileAsync(id, ManagerId, manager: true, ct);
        if (file is null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return file.ContentType == "application/pdf" ? File(file.Bytes, file.ContentType, file.FileName) : File(file.Bytes, file.ContentType);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(Guid id, string? version, CancellationToken ct) => Decide(id, true, null, version, ct);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Reject(Guid id, string? version, string? reason, CancellationToken ct) => Decide(id, false, reason, version, ct);

    private async Task<IActionResult> Decide(Guid id, bool approve, string? reason, string? version, CancellationToken ct)
    {
        var result = await verification.DecideAsync(ManagerId, id, approve, reason, version, ct);
        if (result.NotFound) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }
}
