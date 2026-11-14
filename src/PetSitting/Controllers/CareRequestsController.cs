using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PetSitting.Data;
using PetSitting.Models;
using PetSitting.Services.Requests;
using PetSitting.Services.Scheduling;
using PetSitting.ViewModels;

namespace PetSitting.Controllers;

[Authorize(Roles = Roles.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CareRequestsController(PetSittingDbContext db, UserManager<ApplicationUser> users, TimeProvider clock) : Controller
{
    private Guid OwnerId => Guid.Parse(users.GetUserId(User)!);

    [HttpGet]
    public async Task<IActionResult> Create(Guid? draftId, bool fresh = false)
    {
        var draft = draftId is { } id
            ? await db.OwnerRequestDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.CustomerId == OwnerId)
            : fresh ? null : await db.OwnerRequestDrafts.AsNoTracking().Where(d => d.CustomerId == OwnerId && d.SubmittedRequestId == null).OrderByDescending(d => d.UpdatedAtUtc).FirstOrDefaultAsync();
        if (draftId is not null && draft is null) return NotFound();
        if (draft?.SubmittedRequestId is { } submitted) return RedirectToAction(nameof(Submitted), new { id = submitted });
        var account = await users.GetUserAsync(User);
        var model = draft is null ? new OwnerRequestWizard { Form = new() { City = account?.City ?? "", StreetAddress = account?.HomeAddress is { Length: <= 140 } home ? home : null } } : FromDraft(draft);
        await Populate(model);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid draftId, string? version, int step, string? command, OwnerRequestForm form)
    {
        step = Math.Clamp(step, 1, 6);
        if (draftId == Guid.Empty) return BadRequest();
        var draft = await db.OwnerRequestDrafts.SingleOrDefaultAsync(d => d.Id == draftId);
        if (draft is not null && draft.CustomerId != OwnerId) return NotFound();
        if (draft?.SubmittedRequestId is { } submitted)
            return command == "autosave" ? Json(new { submittedUrl = Url.Action(nameof(Submitted), new { id = submitted }) }) : RedirectToAction(nameof(Submitted), new { id = submitted });
        var model = new OwnerRequestWizard { DraftId = draftId, Form = form, Step = step, Version = version ?? "" };
        if (draft is not null && !string.Equals(Convert.ToBase64String(draft.RowVersion), version, StringComparison.Ordinal))
            return await ConflictView(model, draft, command);
        if (draft is null && !string.IsNullOrEmpty(version)) return NotFound();
        if (form.PetIds.Count > 20) ModelState.AddModelError("Form.PetIds", "Choose at most 20 pets for one request.");
        var submit = command == "submit";
        var next = command == "next";
        await Populate(model);
        if (next || submit)
        {
            for (var current = submit ? 1 : step; current <= (submit ? 6 : step); current++)
            {
                await ValidateStep(model, current);
                if (!ModelState.IsValid) { model.Step = current; break; }
            }
        }
        if (!ModelState.IsValid)
        {
            if (command == "autosave") return BadRequest(new { message = "Some draft fields are invalid. Check the highlighted fields before saving." });
            return View("Create", model);
        }
        var nextStep = next ? Math.Min(6, step + 1) : command == "back" ? Math.Max(1, step - 1)
            : command?.StartsWith("goto:", StringComparison.Ordinal) == true && int.TryParse(command[5..], out var target) ? Math.Clamp(target, 1, step) : step;
        if (draft is null)
        {
            draft = new OwnerRequestDraft { Id = draftId, CustomerId = OwnerId };
            db.OwnerRequestDrafts.Add(draft);
        }
        draft.DataJson = JsonSerializer.Serialize(form);
        draft.Step = nextStep;
        draft.UpdatedAtUtc = clock.GetUtcNow();
        if (submit)
        {
            var times = form.Times()!.Value;
            var names = string.Join(", ", model.Pets.Where(p => form.PetIds.Contains(p.Id)).Select(p => p.Name));
            var title = OwnerRequestForm.ServiceName(form.ServiceType) + " for " + names;
            var request = new PetCareRequest
            {
                Id = draftId, CustomerId = OwnerId, Title = title.Length > 150 ? title[..147] + "…" : title,
                Description = string.IsNullOrWhiteSpace(form.Instructions) ? "Please follow the selected pets’ medical notes, feeding instructions and care routines." : form.Instructions.Trim(),
                ServiceType = form.ServiceType!.Value, StartsAtUtc = times.Start, EndsAtUtc = times.End,
                City = form.City!.Trim(), ServiceAddress = form.ServiceType == ServiceType.Boarding ? null : form.Address,
                Budget = form.BudgetMin!.Value, Currency = "USD", Status = RequestStatus.Open, CreatedAtUtc = clock.GetUtcNow(),
                Pets = form.PetIds.Distinct().Select(id => new PetCareRequestPet { PetId = id }).ToList()
            };
            db.PetCareRequests.Add(request);
            draft.SubmittedRequestId = request.Id;
            if (form.PreferredSitterId is { } sitterId)
            {
                var sitter = model.Sitters.Single(s => s.Id == sitterId);
                db.PetCareOffers.Add(new PetCareOffer { RequestId = request.Id, SitterProfileId = sitter.Id, Amount = request.Budget, Currency = "USD" });
                db.Notifications.Add(new Notification { UserId = sitter.UserId, Category = NotificationCategory.Request, SourceKey = "owner-request:" + request.Id,
                    Title = "A pet owner sent you a sitting request", Body = request.Title + " · " + request.City,
                    RelativeUrl = "/SitterRequests?id=" + request.Id });
            }
        }
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var latest = await db.OwnerRequestDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId && d.CustomerId == OwnerId);
            if (latest.SubmittedRequestId is { } requestId) return RedirectToAction(nameof(Submitted), new { id = requestId });
            return await ConflictView(model, latest, command);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            var latest = await db.OwnerRequestDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == draftId && d.CustomerId == OwnerId);
            if (latest?.SubmittedRequestId is { } requestId) return RedirectToAction(nameof(Submitted), new { id = requestId });
            if (latest is not null) return await ConflictView(model, latest, command);
            throw;
        }
        if (submit) return RedirectToAction(nameof(Submitted), new { id = draft.SubmittedRequestId });
        if (command == "autosave") return Json(new { version = Convert.ToBase64String(draft.RowVersion), savedAt = draft.UpdatedAtUtc, draftId });
        if (command == "exit")
        {
            TempData["Success"] = "Your sitting request draft was saved. Choose New request to pick up where you left off.";
            return RedirectToAction("Customer", "Dashboard");
        }
        return RedirectToAction(nameof(Create), new { draftId });
    }

    private async Task<IActionResult> ConflictView(OwnerRequestWizard model, OwnerRequestDraft latest, string? command)
    {
        if (command == "autosave") return Conflict(new { message = "This draft changed in another tab. Reload to see its latest version." });
        ModelState.AddModelError("", "This draft changed in another tab. Your entries are shown below. Check them before saving again.");
        model.Version = Convert.ToBase64String(latest.RowVersion);
        ModelState.Remove("version");
        await Populate(model);
        return View("Create", model);
    }

    private async Task Populate(OwnerRequestWizard model)
    {
        model.Pets = await db.Pets.AsNoTracking().Where(p => p.OwnerId == OwnerId).OrderBy(p => p.Name).ToListAsync();
        model.Sitters = await EligibleSitters(model.Form);
    }

    private async Task<IReadOnlyList<PetSitterProfile>> EligibleSitters(OwnerRequestForm form)
    {
        if (string.IsNullOrWhiteSpace(form.City) || form.ServiceType is null || !Enum.IsDefined(form.ServiceType.Value)) return [];
        (DateTimeOffset Start, DateTimeOffset End)? times;
        try { times = form.Times(); } catch (ArgumentException) { return []; }
        if (times is null || times.Value.End <= times.Value.Start || times.Value.Start <= clock.GetUtcNow()) return [];
        var candidates = await db.PetSitterProfiles.AsNoTracking().Where(p => p.UserId != OwnerId && p.User.IsActive && p.IsAvailable && p.City == form.City!.Trim()
            && (!p.Services.Any() || p.Services.Any(s => s.ServiceType == form.ServiceType)))
            .Include(p => p.User).Include(p => p.Services).Include(p => p.Availability).Include(p => p.ScheduleExceptions).AsSplitQuery().ToListAsync();
        var ids = candidates.Select(p => p.Id).ToArray();
        var bookings = await db.PetBookings.AsNoTracking().Where(b => ids.Contains(b.SitterProfileId) && b.StartsAtUtc < times.Value.End && b.EndsAtUtc > times.Value.Start).ToListAsync();
        var preview = new PetCareRequest { CustomerId = OwnerId, City = form.City!.Trim(), ServiceType = form.ServiceType.Value, StartsAtUtc = times.Value.Start, EndsAtUtc = times.Value.End };
        return candidates.Where(p => SitterRequestPolicy.AcceptanceBlock(preview, p, bookings.Where(b => b.SitterProfileId == p.Id), clock.GetUtcNow()) is null)
            .OrderByDescending(p => p.VerificationStatus == VerificationStatus.Verified).ThenByDescending(p => p.YearsOfExperience).ThenBy(p => p.User.FirstName).ToArray();
    }

    private Task ValidateStep(OwnerRequestWizard model, int step)
    {
        var form = model.Form;
        void Error(string key, string message) => ModelState.AddModelError("Form." + key, message);
        switch (step)
        {
            case 1:
                if (form.PetIds.Count == 0) Error("PetIds", "Select at least one pet.");
                if (form.PetIds.Distinct().Count() != form.PetIds.Count || form.PetIds.Any(id => model.Pets.All(p => p.Id != id))) Error("PetIds", "Choose only pets from your own profiles.");
                break;
            case 2:
                if (form.ServiceType is null || !Enum.IsDefined(form.ServiceType.Value)) Error("ServiceType", "Choose a service.");
                if (form.ServiceType == ServiceType.DogWalking && model.Pets.Any(p => form.PetIds.Contains(p.Id) && p.Species != "Dog")) Error("ServiceType", "Dog walking is available for dogs only. Change the selected pets or choose another service.");
                break;
            case 3:
                try
                {
                    var times = form.Times();
                    if (times is null) Error("StartDate", "Choose start and end dates and times.");
                    else if (times.Value.Start <= clock.GetUtcNow()) Error("StartDate", "Care must start in the future.");
                    else if (times.Value.End <= times.Value.Start) Error("EndDate", "Care must end after it starts.");
                    else if ((times.Value.End - times.Value.Start).TotalDays > 14) Error("EndDate", "A request can last at most 14 days.");
                }
                catch (ArgumentException) { Error("StartTime", "This local time is unavailable due to daylight saving. Choose another time."); }
                break;
            case 4:
                if (string.IsNullOrWhiteSpace(form.City)) Error("City", "Enter the city where care is needed.");
                if (form.ServiceType != ServiceType.Boarding && string.IsNullOrWhiteSpace(form.StreetAddress)) Error("StreetAddress", "Enter your street address.");
                if (form.Address.Length > 300) Error("StreetAddress", "Keep the combined address, building, area and access notes within 300 characters.");
                break;
            case 5:
                if (form.BudgetMin is null || form.BudgetMax is null) Error("BudgetMin", "Enter your total budget range in USD.");
                else if (form.BudgetMax < form.BudgetMin) Error("BudgetMax", "The maximum budget must be at least the minimum.");
                if (form.PreferredSitterId is { } sitter && model.Sitters.All(s => s.Id != sitter)) Error("PreferredSitterId", "That sitter is no longer available for these details. Choose another sitter or leave the preference empty.");
                break;
            case 6:
                if (!form.CareConfirmed) Error("CareConfirmed", "Confirm you’ve reviewed the pets’ care notes and request details.");
                break;
        }
        return Task.CompletedTask;
    }

    private static OwnerRequestWizard FromDraft(OwnerRequestDraft draft) => new()
    {
        DraftId = draft.Id, Version = Convert.ToBase64String(draft.RowVersion), Step = draft.Step,
        Form = JsonSerializer.Deserialize<OwnerRequestForm>(draft.DataJson) ?? new(), SavedAt = draft.UpdatedAtUtc
    };

    [HttpGet]
    public async Task<IActionResult> Submitted(Guid id)
    {
        var result = await LoadResult(id);
        return result is null ? NotFound() : View(result);
    }
    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var result = await LoadResult(id);
        return result is null ? NotFound() : RedirectToAction("Details", "OwnerBookings", new { id });
    }
    [HttpGet]
    public async Task<IActionResult> Status(Guid id)
    {
        var result = await LoadResult(id);
        return result is null ? NotFound() : PartialView("_Status", result);
    }
    private async Task<OwnerRequestResult?> LoadResult(Guid id)
    {
        var request = await db.PetCareRequests.AsNoTracking().Where(r => r.Id == id && r.CustomerId == OwnerId).Include(r => r.Pets).ThenInclude(p => p.Pet).SingleOrDefaultAsync();
        if (request is null) return null;
        var json = await db.OwnerRequestDrafts.Where(d => d.SubmittedRequestId == id && d.CustomerId == OwnerId).Select(d => d.DataJson).SingleOrDefaultAsync();
        var form = json is null ? null : JsonSerializer.Deserialize<OwnerRequestForm>(json);
        return new() { Request = request, Form = form,
            PreferredSitter = form?.PreferredSitterId is { } sitter ? await db.PetSitterProfiles.AsNoTracking().Include(p => p.User).SingleOrDefaultAsync(p => p.Id == sitter) : null,
            Booking = await db.PetBookings.AsNoTracking().Where(b => b.RequestId == id).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefaultAsync() };
    }
}
