using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PetSitting.Models;
using PetSitting.Repositories;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

// My Pets: CRUD for the signed-in pet owner.
// GET actions display pages; POST actions change data and then redirect (Post/Redirect/Get).
[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PetsController(IPetRepository repository, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid OwnerId => Guid.Parse(userManager.GetUserId(User)!);

    // GET /Pets?search=max&species=Dog
    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? species)
    {
        ViewBag.Search = search;
        ViewBag.Species = species;
        var pets = await repository.GetAllForOwnerAsync(OwnerId, search, species);
        return View(pets);
    }

    // GET /Pets/Details/{id}
    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var pet = await repository.GetByIdAsync(id, OwnerId);
        return pet is null ? NotFound() : View(pet);
    }

    // GET /Pets/Create
    [HttpGet]
    public IActionResult Create() => View(new PetFormViewModel());

    // POST /Pets/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PetFormViewModel form)
    {
        ValidateSpecies(form);
        if (!ModelState.IsValid)
            return View(form);

        var pet = new Pet { OwnerId = OwnerId };
        CopyToPet(form, pet);
        await repository.AddAsync(pet);

        TempData["Success"] = $"{pet.Name} was added to your pets.";
        return RedirectToAction(nameof(Details), new { id = pet.Id });
    }

    // GET /Pets/Edit/{id}
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var pet = await repository.GetByIdAsync(id, OwnerId);
        if (pet is null) return NotFound();

        ViewBag.PetId = pet.Id;
        return View(ToForm(pet));
    }

    // POST /Pets/Edit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, PetFormViewModel form)
    {
        ValidateSpecies(form);
        if (!ModelState.IsValid)
        {
            ViewBag.PetId = id;
            return View(form);
        }

        var pet = new Pet { Id = id, OwnerId = OwnerId };
        CopyToPet(form, pet);
        if (!await repository.UpdateAsync(pet)) return NotFound();

        TempData["Success"] = $"{pet.Name}'s profile was updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Pets/Delete/{id} — confirmation page; never delete on a GET request.
    [HttpGet]
    public async Task<IActionResult> Delete(Guid id)
    {
        var pet = await repository.GetByIdAsync(id, OwnerId);
        if (pet is null) return NotFound();

        ViewBag.IsUsed = await repository.IsUsedInCareRequestsAsync(id);
        return View(pet);
    }

    // POST /Pets/Delete/{id}
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id)
    {
        var pet = await repository.GetByIdAsync(id, OwnerId);
        if (pet is null) return NotFound();

        // Pets that appear in care requests keep their history, so they cannot be removed.
        if (await repository.IsUsedInCareRequestsAsync(id))
        {
            TempData["Error"] = $"{pet.Name} is part of a care request and can't be deleted.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await repository.DeleteAsync(id, OwnerId);
        TempData["Success"] = $"{pet.Name} was removed from your pets.";
        return RedirectToAction(nameof(Index));
    }

    private void ValidateSpecies(PetFormViewModel form)
    {
        if (!string.IsNullOrEmpty(form.Species) && !PetFormViewModel.SpeciesOptions.Contains(form.Species))
            ModelState.AddModelError(nameof(form.Species), "Please choose a species from the list.");
    }

    private static PetFormViewModel ToForm(Pet pet) => new()
    {
        Name = pet.Name,
        Species = pet.Species,
        Breed = pet.Breed,
        AgeYears = pet.AgeYears,
        MedicalNotes = pet.MedicalNotes,
        FeedingInstructions = pet.FeedingInstructions,
        CareInstructions = pet.CareInstructions
    };

    private static void CopyToPet(PetFormViewModel form, Pet pet)
    {
        pet.Name = form.Name.Trim();
        pet.Species = form.Species;
        pet.Breed = string.IsNullOrWhiteSpace(form.Breed) ? null : form.Breed.Trim();
        pet.AgeYears = form.AgeYears;
        pet.MedicalNotes = string.IsNullOrWhiteSpace(form.MedicalNotes) ? null : form.MedicalNotes.Trim();
        pet.FeedingInstructions = string.IsNullOrWhiteSpace(form.FeedingInstructions) ? null : form.FeedingInstructions.Trim();
        pet.CareInstructions = string.IsNullOrWhiteSpace(form.CareInstructions) ? null : form.CareInstructions.Trim();
    }
}
