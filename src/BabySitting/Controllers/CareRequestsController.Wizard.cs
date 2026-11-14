using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BabySitting.Models;
using BabySitting.Services.Requests;
using BabySitting.ViewModels;

namespace BabySitting.Controllers;

// Six-step "create a request" wizard with saved drafts (mirrors Ramy's PetSitting wizard).
// Children are described by count and age range from the family profile.
public sealed partial class CareRequestsController
{
    [HttpGet]
    public async Task<IActionResult> Create(Guid? draftId, bool fresh = false)
    {
        var draft = draftId is { } id
            ? await Db.ParentRequestDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.CustomerId == UserId)
            : fresh ? null : await Db.ParentRequestDrafts.AsNoTracking().Where(d => d.CustomerId == UserId && d.SubmittedRequestId == null).OrderByDescending(d => d.UpdatedAtUtc).FirstOrDefaultAsync();
        if (draftId is not null && draft is null) return NotFound();
        if (draft?.SubmittedRequestId is { } submitted) return RedirectToAction(nameof(Submitted), new { id = submitted });
        var model = draft is null ? await NewWizard() : FromDraft(draft);
        await Populate(model);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid draftId, string? version, int step, string? command, ParentRequestForm form)
    {
        step = Math.Clamp(step, 1, 6);
        if (draftId == Guid.Empty) return BadRequest();
        var draft = await Db.ParentRequestDrafts.SingleOrDefaultAsync(d => d.Id == draftId);
        if (draft is not null && draft.CustomerId != UserId) return NotFound();
        if (draft?.SubmittedRequestId is { } submitted)
            return command == "autosave" ? Json(new { submittedUrl = Url.Action(nameof(Submitted), new { id = submitted }) }) : RedirectToAction(nameof(Submitted), new { id = submitted });
        var model = new ParentRequestWizard { DraftId = draftId, Form = form, Step = step, Version = version ?? "" };
        if (draft is not null && !string.Equals(Convert.ToBase64String(draft.RowVersion), version, StringComparison.Ordinal))
            return await ConflictView(model, draft, command);
        if (draft is null && !string.IsNullOrEmpty(version)) return NotFound();
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
            draft = new ParentRequestDraft { Id = draftId, CustomerId = UserId };
            Db.ParentRequestDrafts.Add(draft);
        }
        draft.DataJson = JsonSerializer.Serialize(form);
        draft.Step = nextStep;
        draft.UpdatedAtUtc = Clock.GetUtcNow();
        if (submit)
        {
            var times = form.Times()!.Value;
            var title = ParentRequestForm.ServiceName(form.ServiceType) + " for " + DashboardViewModel.Children(form.NumberOfChildren, form.YoungestChildAgeYears, form.OldestChildAgeYears);
            var request = new ChildcareRequest
            {
                Id = draftId, CustomerId = UserId, Title = title.Length > 150 ? title[..147] + "…" : title,
                Description = string.IsNullOrWhiteSpace(form.Instructions) ? "Please follow our family’s health notes and routines." : form.Instructions.Trim(),
                ServiceType = form.ServiceType!.Value, StartsAtUtc = times.Start, EndsAtUtc = times.End,
                City = form.City!.Trim(), ServiceAddress = string.IsNullOrWhiteSpace(form.Address) ? null : form.Address,
                Budget = form.BudgetMin!.Value, Currency = "USD", Status = RequestStatus.Open, CreatedAtUtc = Clock.GetUtcNow(),
                FamilyProfileId = model.Family!.Id, CorrelationId = Guid.NewGuid(), NumberOfChildren = form.NumberOfChildren,
                YoungestChildAgeYears = form.YoungestChildAgeYears, OldestChildAgeYears = form.OldestChildAgeYears
            };
            Db.ChildcareRequests.Add(request);
            draft.SubmittedRequestId = request.Id;
            if (form.PreferredSitterId is { } sitterId)
            {
                var sitter = model.Sitters.Single(s => s.Id == sitterId);
                Db.BabysittingOffers.Add(new BabysittingOffer { RequestId = request.Id, SitterProfileId = sitter.Id, Amount = request.Budget, Currency = "USD" });
                Db.Notifications.Add(new Notification { UserId = sitter.UserId, Category = NotificationCategory.Request, SourceKey = "owner-request:" + request.Id,
                    Title = "A parent sent you a childcare request", Body = request.Title + " · " + request.City,
                    RelativeUrl = "/SitterRequests?id=" + request.Id });
            }
        }
        try { await Db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            Db.ChangeTracker.Clear();
            var latest = await Db.ParentRequestDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId && d.CustomerId == UserId);
            if (latest.SubmittedRequestId is { } requestId) return RedirectToAction(nameof(Submitted), new { id = requestId });
            return await ConflictView(model, latest, command);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            Db.ChangeTracker.Clear();
            var latest = await Db.ParentRequestDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == draftId && d.CustomerId == UserId);
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

    private async Task<IActionResult> ConflictView(ParentRequestWizard model, ParentRequestDraft latest, string? command)
    {
        if (command == "autosave") return Conflict(new { message = "This draft changed in another tab. Reload to see its latest version." });
        ModelState.AddModelError("", "This draft changed in another tab. Your entries are shown below. Check them before saving again.");
        model.Version = Convert.ToBase64String(latest.RowVersion);
        ModelState.Remove("version");
        await Populate(model);
        return View("Create", model);
    }

    private async Task Populate(ParentRequestWizard model)
    {
        model.Family = await Db.FamilyProfiles.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == UserId);
        model.Sitters = await EligibleSitters(model.Form);
    }

    private async Task<IReadOnlyList<BabysitterProfile>> EligibleSitters(ParentRequestForm form)
    {
        if (string.IsNullOrWhiteSpace(form.City) || form.ServiceType is null || !Enum.IsDefined(form.ServiceType.Value)) return [];
        (DateTimeOffset Start, DateTimeOffset End)? times;
        try { times = form.Times(); } catch (ArgumentException) { return []; }
        if (times is null || times.Value.End <= times.Value.Start || times.Value.Start <= Clock.GetUtcNow()) return [];
        var candidates = await Db.BabysitterProfiles.AsNoTracking().Where(p => p.UserId != UserId && p.User.IsActive && p.IsAvailable && p.City == form.City!.Trim()
            && (!p.Services.Any() || p.Services.Any(s => s.ServiceType == form.ServiceType)))
            .Include(p => p.User).Include(p => p.Services).Include(p => p.Availability).Include(p => p.ScheduleExceptions).AsSplitQuery().ToListAsync();
        var ids = candidates.Select(p => p.Id).ToArray();
        var bookings = await Db.BabysittingBookings.AsNoTracking().Where(b => ids.Contains(b.SitterProfileId) && b.StartsAtUtc < times.Value.End && b.EndsAtUtc > times.Value.Start).ToListAsync();
        var preview = new ChildcareRequest { CustomerId = UserId, City = form.City!.Trim(), ServiceType = form.ServiceType.Value, StartsAtUtc = times.Value.Start, EndsAtUtc = times.Value.End };
        return candidates.Where(p => SitterRequestPolicy.AcceptanceBlock(preview, p, bookings.Where(b => b.SitterProfileId == p.Id), Clock.GetUtcNow()) is null)
            .OrderByDescending(p => p.VerificationStatus == VerificationStatus.Verified).ThenByDescending(p => p.YearsOfExperience).ThenBy(p => p.User.FirstName).ToArray();
    }

    private Task ValidateStep(ParentRequestWizard model, int step)
    {
        var form = model.Form;
        void Error(string key, string message) => ModelState.AddModelError("Form." + key, message);
        switch (step)
        {
            case 1:
                if (model.Family is null) Error("NumberOfChildren", "Create your family profile before sending a request.");
                if (form.NumberOfChildren is < 1 or > 20) Error("NumberOfChildren", "Number of children must be between 1 and 20.");
                if (form.YoungestChildAgeYears is < 0 or > 17 || form.OldestChildAgeYears is < 0 or > 17) Error("YoungestChildAgeYears", "Ages must be between 0 and 17 years.");
                else if (form.OldestChildAgeYears < form.YoungestChildAgeYears) Error("OldestChildAgeYears", "The oldest child can't be younger than the youngest child.");
                else if (form.NumberOfChildren == 1 && form.OldestChildAgeYears != form.YoungestChildAgeYears) Error("OldestChildAgeYears", "With one child, the youngest and oldest ages should be the same.");
                break;
            case 2:
                if (form.ServiceType is null || !Enum.IsDefined(form.ServiceType.Value)) Error("ServiceType", "Choose a service.");
                break;
            case 3:
                try
                {
                    var times = form.Times();
                    if (times is null) Error("StartDate", "Choose start and end dates and times.");
                    else if (times.Value.Start <= Clock.GetUtcNow()) Error("StartDate", "Care must start in the future.");
                    else if (times.Value.End <= times.Value.Start) Error("EndDate", "Care must end after it starts.");
                    else if ((times.Value.End - times.Value.Start).TotalDays > 14) Error("EndDate", "A request can last at most 14 days.");
                }
                catch (ArgumentException) { Error("StartTime", "This local time is unavailable due to daylight saving. Choose another time."); }
                break;
            case 4:
                if (string.IsNullOrWhiteSpace(form.City)) Error("City", "Enter the city where care is needed.");
                if (string.IsNullOrWhiteSpace(form.StreetAddress)) Error("StreetAddress", "Enter your street address.");
                if (form.Address.Length > 300) Error("StreetAddress", "Keep the combined address, building, area and access notes within 300 characters.");
                break;
            case 5:
                if (form.BudgetMin is null || form.BudgetMax is null) Error("BudgetMin", "Enter your total budget range in USD.");
                else if (form.BudgetMax < form.BudgetMin) Error("BudgetMax", "The maximum budget must be at least the minimum.");
                if (form.PreferredSitterId is { } sitter && model.Sitters.All(s => s.Id != sitter)) Error("PreferredSitterId", "That sitter is no longer available for these details. Choose another sitter or leave the preference empty.");
                break;
            case 6:
                if (!form.CareConfirmed) Error("CareConfirmed", "Confirm you’ve reviewed your family’s care notes and request details.");
                break;
        }
        return Task.CompletedTask;
    }

    private static ParentRequestWizard FromDraft(ParentRequestDraft draft) => new()
    {
        DraftId = draft.Id, Version = Convert.ToBase64String(draft.RowVersion), Step = draft.Step,
        Form = JsonSerializer.Deserialize<ParentRequestForm>(draft.DataJson) ?? new(), SavedAt = draft.UpdatedAtUtc
    };

    [HttpGet]
    public async Task<IActionResult> Submitted(Guid id)
    {
        var result = await LoadResult(id);
        return result is null ? NotFound() : View(result);
    }
    [HttpGet]
    public async Task<IActionResult> Status(Guid id)
    {
        var result = await LoadResult(id);
        return result is null ? NotFound() : PartialView("_Status", result);
    }
    private async Task<ParentRequestResult?> LoadResult(Guid id)
    {
        var request = await Db.ChildcareRequests.AsNoTracking().Where(r => r.Id == id && r.CustomerId == UserId).Include(r => r.FamilyProfile).SingleOrDefaultAsync();
        if (request is null) return null;
        var json = await Db.ParentRequestDrafts.Where(d => d.SubmittedRequestId == id && d.CustomerId == UserId).Select(d => d.DataJson).SingleOrDefaultAsync();
        var form = json is null ? null : JsonSerializer.Deserialize<ParentRequestForm>(json);
        return new() { Request = request, Form = form,
            PreferredSitter = form?.PreferredSitterId is { } sitter ? await Db.BabysitterProfiles.AsNoTracking().Include(p => p.User).SingleOrDefaultAsync(p => p.Id == sitter) : null,
            Booking = await Db.BabysittingBookings.AsNoTracking().Where(b => b.RequestId == id).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefaultAsync() };
    }

    private async Task<ParentRequestWizard> NewWizard()
    {
        var family = await Db.FamilyProfiles.AsNoTracking().SingleOrDefaultAsync(f => f.UserId == UserId);
        var city = family?.City ?? (await Users.GetUserAsync(User))?.City ?? "";
        return new ParentRequestWizard { Form = new() { City = city, NumberOfChildren = family?.NumberOfChildren ?? 1,
            YoungestChildAgeYears = family?.YoungestChildAgeYears ?? 0, OldestChildAgeYears = family?.OldestChildAgeYears ?? 0 } };
    }
}
