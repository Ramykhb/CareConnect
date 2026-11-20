using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Portal;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

// The sitter's verification page: send a photo or PDF of an ID or certificate, then see the manager's decision.
[Authorize(Roles = Roles.Sitter)]
[Route("Sitter/Verification")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SitterVerificationController(ISitterVerificationService verification, UserManager<ApplicationUser> users) : Controller
{
    private Guid UserId => Guid.Parse(users.GetUserId(User)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var account = await users.GetUserAsync(User);
        return account is null ? Challenge() : View(await verification.PageAsync(account, ct));
    }

    [HttpPost(""), ValidateAntiForgeryToken, RequestSizeLimit(5_600_000), RequestFormLimits(MultipartBodyLengthLimit = 5_600_000)]
    public async Task<IActionResult> Submit([Bind(Prefix = "Form")] VerificationForm form, IFormFile? document, CancellationToken ct)
    {
        PortalWriteResult result;
        if (!ModelState.IsValid) result = new(false, "Check the document type and note, then try again.");
        else
        {
            await using var stream = document?.OpenReadStream();
            result = await verification.SubmitAsync(UserId, form, stream, document?.FileName, document?.Length ?? 0, ct);
        }
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("File/{id:guid}")]
    public async Task<IActionResult> Document(Guid id, CancellationToken ct) =>
        Serve(await verification.FileAsync(id, UserId, manager: false, ct));

    private IActionResult Serve(VerificationFile? file)
    {
        if (file is null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return file.ContentType == "application/pdf" ? File(file.Bytes, file.ContentType, file.FileName) : File(file.Bytes, file.ContentType);
    }
}
