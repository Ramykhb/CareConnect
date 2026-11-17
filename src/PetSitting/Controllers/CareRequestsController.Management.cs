using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PetSitting.Models;
using PetSitting.Repositories;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

public sealed partial class CareRequestsController
{
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var request = await requests.FindAsync(OwnerId, id);
        if (request is null) return NotFound();
        if (!PetCareRequestRepository.IsEditable(request, clock.GetUtcNow()))
            return ManagementResult(RequestWriteResult.NotEditable, id);
        return View(new RequestEditViewModel
        {
            Version = Convert.ToBase64String(request.RowVersion), Title = request.Title, Description = request.Description,
            ServiceType = request.ServiceType, PetIds = request.Pets.Select(p => p.PetId).ToList(),
            StartsAtLocal = DashboardViewModel.Local(request.StartsAtUtc).DateTime, EndsAtLocal = DashboardViewModel.Local(request.EndsAtUtc).DateTime,
            City = request.City, ServiceAddress = request.ServiceAddress, Budget = request.Budget, Currency = request.Currency,
            AvailablePets = await EditablePets()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, RequestEditViewModel form)
    {
        var request = await requests.FindAsync(OwnerId, id);
        if (request is null) return NotFound();
        if (!PetCareRequestRepository.IsEditable(request, clock.GetUtcNow())) return ManagementResult(RequestWriteResult.NotEditable, id);
        form.AvailablePets = await EditablePets(); form.Currency = request.Currency;
        if (form.PetIds.Any(p => form.AvailablePets.All(owned => owned.Id != p)))
            ModelState.AddModelError(nameof(form.PetIds), "Choose only pets from your own profiles.");
        if (form.ServiceType == ServiceType.DogWalking && form.AvailablePets.Any(p => form.PetIds.Contains(p.Id) && p.Species != "Dog"))
            ModelState.AddModelError(nameof(form.ServiceType), "Dog walking is available for dogs only.");
        if (!ModelState.IsValid) return View(form);
        return ManagementResult(await requests.UpdateAsync(OwnerId, id, form), id);
    }

    [HttpGet]
    public async Task<IActionResult> Delete(Guid id)
    {
        var request = await requests.FindAsync(OwnerId, id);
        if (request is null) return NotFound();
        if (request.Status != RequestStatus.Open) return ManagementResult(RequestWriteResult.NotEditable, id);
        ViewBag.IsCombinedCare = await db.CombinedCareLinks.AnyAsync(l => l.LocalRequestId == id);
        return View(request);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id, string? version) =>
        ManagementResult(await requests.DeleteAsync(OwnerId, id, version ?? ""), id);

    private async Task<IReadOnlyList<Pet>> EditablePets() => await db.Pets.AsNoTracking().Where(p => p.OwnerId == OwnerId).OrderBy(p => p.Name).ToListAsync();

    private IActionResult ManagementResult(RequestWriteResult result, Guid id)
    {
        if (result == RequestWriteResult.NotFound) return NotFound();
        var success = result is RequestWriteResult.Saved or RequestWriteResult.Deleted or RequestWriteResult.Cancelled;
        TempData[success ? "Success" : "Error"] = result switch
        {
            RequestWriteResult.Saved => "Your request was updated. Sitters must review the latest details before accepting.",
            RequestWriteResult.Deleted => "Your request was deleted.",
            RequestWriteResult.Cancelled => "Your request was cancelled. Existing history and combined-care links were preserved.",
            RequestWriteResult.NotEditable => "Only open requests can be deleted, and only future open requests can be edited. Manage accepted bookings from their booking page.",
            RequestWriteResult.InvalidPets => "The selected pets are no longer valid. Review your request and try again.",
            _ => "This request changed. Review its latest details and try again."
        };
        return result == RequestWriteResult.Deleted ? RedirectToAction("Index", "OwnerBookings") : RedirectToAction("Details", "OwnerBookings", new { id });
    }
}
