using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Services.Bookings;

namespace PetSitting.Controllers;

[Authorize(Roles = Roles.Customer + "," + Roles.Sitter)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BookingPhotosController(ISitterBookingService bookings, UserManager<ApplicationUser> users) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Image(Guid id, CancellationToken ct)
    {
        var photo = await bookings.PhotoAsync(Guid.Parse(users.GetUserId(User)!), id, ct);
        if (photo is null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return File(photo.Bytes, photo.ContentType);
    }
}
