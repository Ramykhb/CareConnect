using CareConnect.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BabySitting.Models;

namespace BabySitting.Controllers;

[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CareSummariesController(ICareSummaryService summaries, UserManager<ApplicationUser> users) : Controller
{
    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("care-summaries")]
    public async Task<IActionResult> Generate(Guid bookingId, string scope, DateOnly? date)
    {
        if (!ModelState.IsValid || bookingId == Guid.Empty || scope is not ("day" or "all") || scope == "day" && date is null)
            return BadRequest(new { message = "Choose a valid date or the entire booking." });
        try
        {
            var result = await summaries.GenerateAsync(Guid.Parse(users.GetUserId(User)!), bookingId, scope == "day" ? date : null, HttpContext.RequestAborted);
            return result is null ? NotFound() : Json(result);
        }
        catch (AssistantException e) { return BadRequest(new { message = e.Message }); }
    }
}
