using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using BabySitting.Models;
using BabySitting.Repositories;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

// My Family: create, view, edit and delete the signed-in parent's family profile.
// A parent has at most one profile, so the actions use the signed-in user instead of an id in the URL.
// GET actions display pages; POST actions change data and then redirect (Post/Redirect/Get).
[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FamilyController(IFamilyRepository repository, UserManager<ApplicationUser> userManager) : Controller
{
    private Guid UserId => Guid.Parse(userManager.GetUserId(User)!);

    // GET /Family — shows the profile, or an empty state with a "Create" button.
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var family = await repository.GetForUserAsync(UserId);
        return View(family);
    }

    // GET /Family/Create
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (await repository.GetForUserAsync(UserId) is not null)
            return RedirectToAction(nameof(Edit));

        var user = await userManager.GetUserAsync(User);
        return View(new FamilyFormViewModel { City = user?.City ?? string.Empty });
    }

    // POST /Family/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(FamilyFormViewModel form)
    {
        if (await repository.GetForUserAsync(UserId) is not null)
            return RedirectToAction(nameof(Edit));
        if (!ModelState.IsValid)
            return View(form);

        var family = new FamilyProfile { UserId = UserId };
        CopyToFamily(form, family);
        await repository.AddAsync(family);

        TempData["Success"] = "Your family profile was created.";
        return RedirectToAction(nameof(Index));
    }

    // GET /Family/Edit
    [HttpGet]
    public async Task<IActionResult> Edit()
    {
        var family = await repository.GetForUserAsync(UserId);
        return family is null ? RedirectToAction(nameof(Create)) : View(ToForm(family));
    }

    // POST /Family/Edit
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(FamilyFormViewModel form)
    {
        if (!ModelState.IsValid)
            return View(form);

        var family = new FamilyProfile { UserId = UserId };
        CopyToFamily(form, family);
        if (!await repository.UpdateAsync(family)) return NotFound();

        TempData["Success"] = "Your family profile was updated.";
        return RedirectToAction(nameof(Index));
    }

    // GET /Family/Delete — confirmation page; never delete on a GET request.
    [HttpGet]
    public async Task<IActionResult> Delete()
    {
        var family = await repository.GetForUserAsync(UserId);
        if (family is null) return RedirectToAction(nameof(Index));

        ViewBag.IsUsed = await repository.IsUsedInRequestsAsync(family.Id);
        return View(family);
    }

    // POST /Family/Delete
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed()
    {
        var family = await repository.GetForUserAsync(UserId);
        if (family is null) return RedirectToAction(nameof(Index));

        // Profiles linked to childcare requests keep their history, so they cannot be removed.
        if (await repository.IsUsedInRequestsAsync(family.Id))
        {
            TempData["Error"] = "Your family profile is linked to a childcare request and can't be deleted.";
            return RedirectToAction(nameof(Index));
        }

        await repository.DeleteAsync(UserId);
        TempData["Success"] = "Your family profile was deleted.";
        return RedirectToAction(nameof(Index));
    }

    private static FamilyFormViewModel ToForm(FamilyProfile family) => new()
    {
        NumberOfChildren = family.NumberOfChildren,
        YoungestChildAgeYears = family.YoungestChildAgeYears,
        OldestChildAgeYears = family.OldestChildAgeYears,
        City = family.City,
        HealthNotes = family.HealthNotes,
        Routines = family.Routines,
        CarePreferences = family.CarePreferences
    };

    private static void CopyToFamily(FamilyFormViewModel form, FamilyProfile family)
    {
        family.NumberOfChildren = form.NumberOfChildren;
        family.YoungestChildAgeYears = form.YoungestChildAgeYears;
        family.OldestChildAgeYears = form.OldestChildAgeYears;
        family.City = form.City.Trim();
        family.HealthNotes = string.IsNullOrWhiteSpace(form.HealthNotes) ? null : form.HealthNotes.Trim();
        family.Routines = string.IsNullOrWhiteSpace(form.Routines) ? null : form.Routines.Trim();
        family.CarePreferences = string.IsNullOrWhiteSpace(form.CarePreferences) ? null : form.CarePreferences.Trim();
    }
}
