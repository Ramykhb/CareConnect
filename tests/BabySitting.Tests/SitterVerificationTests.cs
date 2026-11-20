using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BabySitting.Data;
using BabySitting.Models;
using BabySitting.Services.Auth;
using BabySitting.Services.Portal;
using BabySitting.ViewModels;
using Xunit;

namespace BabySitting.Tests;

// #4: a sitter sends proof, a platform manager approves or rejects it, and the sitter shows as Verified.
public sealed class SitterVerificationTests
{
    private const string Password = "Test!Password123";
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\n%%EOF");
    private static readonly byte[] Jpeg = [255, 216, 255, 224, 0, 0, 255, 217];

    // ----- Plain checks (no database) -----

    [Fact]
    public void Only_photos_and_pdfs_are_accepted()
    {
        Assert.Equal("application/pdf", SitterVerificationService.FileType(Pdf));
        Assert.Equal("image/png", SitterVerificationService.FileType(Png));
        Assert.Equal("image/jpeg", SitterVerificationService.FileType(Jpeg));
        Assert.Null(SitterVerificationService.FileType(Encoding.ASCII.GetBytes("MZ this is a program")));
        Assert.Null(SitterVerificationService.FileType(Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>")));
        Assert.Null(SitterVerificationService.FileType([]));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\id-card.png", "image/png", "id-card.png")]
    [InlineData("../../etc/passwd.pdf", "application/pdf", "passwd.pdf")]
    [InlineData("my \"best\" id.pdf", "application/pdf", "my best id.pdf")]
    [InlineData("", "application/pdf", "document.pdf")]
    [InlineData(null, "image/png", "document")]
    public void File_names_are_cleaned(string? name, string type, string expected) =>
        Assert.Equal(expected, SitterVerificationService.CleanName(name, type));

    [Fact]
    public void Long_file_names_are_shortened_and_every_document_has_a_label()
    {
        Assert.Equal(150, SitterVerificationService.CleanName(new string('a', 300) + ".pdf", "application/pdf").Length);
        foreach (var type in Enum.GetValues<VerificationDocument>()) Assert.False(string.IsNullOrWhiteSpace(SitterVerificationService.DocumentLabel(type)));
        Assert.Equal("First aid or childcare certificate", SitterVerificationService.DocumentLabel(VerificationDocument.Certificate));
    }

    // ----- Service with the real database -----

    [SqlServerFact]
    public async Task A_sitter_sends_proof_and_a_manager_approves_it()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var (db, service) = (scope.ServiceProvider.GetRequiredService<BabySittingDbContext>(), scope.ServiceProvider.GetRequiredService<ISitterVerificationService>());
        var people = await People.CreateAsync(scope.ServiceProvider);
        try
        {
            var sent = await service.SubmitAsync(people.Sitter, new VerificationForm { DocumentType = VerificationDocument.Passport, Note = "  Here is my passport  " }, new MemoryStream(Png), "passport.png", Png.Length);
            Assert.True(sent.Succeeded, sent.Message);
            var saved = await db.SitterVerifications.AsNoTracking().SingleAsync(v => v.SitterProfileId == people.Profile);
            Assert.Equal((VerificationStatus.Pending, "image/png", "passport.png", "Here is my passport"), (saved.Status, saved.ContentType, saved.FileName, saved.Note));
            Assert.Equal(VerificationStatus.Pending, await ProfileStatus(db, people.Profile));
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == people.Manager && n.SourceKey == "verification:" + saved.Id));

            var again = await service.SubmitAsync(people.Sitter, new VerificationForm(), new MemoryStream(Pdf), "id.pdf", Pdf.Length);
            Assert.False(again.Succeeded);                                                   // one request waits at a time
            Assert.Contains("already waiting", again.Message);

            var item = Assert.Single(await service.ListAsync(VerificationStatus.Pending), i => i.Id == saved.Id);
            Assert.Equal("Sam VerifyTest", item.SitterName);
            Assert.False((await service.DecideAsync(people.Manager, saved.Id, true, null, "AAAAAAAAAAA=")).Succeeded);   // stale version
            var approved = await service.DecideAsync(people.Manager, saved.Id, true, null, item.Version);
            Assert.True(approved.Succeeded, approved.Message);
            db.ChangeTracker.Clear();
            var profile = await db.BabysitterProfiles.AsNoTracking().SingleAsync(p => p.Id == people.Profile);
            Assert.Equal(VerificationStatus.Verified, profile.VerificationStatus);
            Assert.NotNull(profile.VerifiedAtUtc);
            Assert.Equal(people.Manager, (await db.SitterVerifications.AsNoTracking().SingleAsync(v => v.Id == saved.Id)).ReviewedById);
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == people.Sitter && n.Title == "You're verified"));
            Assert.False((await service.DecideAsync(people.Manager, saved.Id, false, "late", item.Version)).Succeeded);   // already decided
            var verifiedAgain = await service.SubmitAsync(people.Sitter, new VerificationForm(), new MemoryStream(Pdf), "id.pdf", Pdf.Length);
            Assert.Contains("already verified", verifiedAgain.Message);
            Assert.True((await service.DecideAsync(people.Manager, Guid.NewGuid(), true, null, item.Version)).NotFound);
        }
        finally { await Cleanup(scope.ServiceProvider, people); }
    }

    [SqlServerFact]
    public async Task A_rejected_sitter_learns_why_and_can_send_new_proof()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var (db, service) = (scope.ServiceProvider.GetRequiredService<BabySittingDbContext>(), scope.ServiceProvider.GetRequiredService<ISitterVerificationService>());
        var people = await People.CreateAsync(scope.ServiceProvider);
        try
        {
            Assert.True((await service.SubmitAsync(people.Sitter, new VerificationForm { DocumentType = VerificationDocument.Certificate }, new MemoryStream(Pdf), "cert.pdf", Pdf.Length)).Succeeded);
            var first = Assert.Single(await service.ListAsync(null), i => i.SitterEmail == people.SitterEmail);
            var noReason = await service.DecideAsync(people.Manager, first.Id, false, "   ", first.Version);
            Assert.False(noReason.Succeeded);
            Assert.Contains("reason", noReason.Message);
            Assert.True((await service.DecideAsync(people.Manager, first.Id, false, "The photo is blurry.", first.Version)).Succeeded);
            db.ChangeTracker.Clear();
            Assert.Equal(VerificationStatus.Rejected, await ProfileStatus(db, people.Profile));
            var alert = await db.Notifications.AsNoTracking().SingleAsync(n => n.UserId == people.Sitter && n.Title == "Verification not approved");
            Assert.Contains("The photo is blurry.", alert.Body);

            Assert.True((await service.SubmitAsync(people.Sitter, new VerificationForm(), new MemoryStream(Png), "new.png", Png.Length)).Succeeded);
            db.ChangeTracker.Clear();
            Assert.Equal(VerificationStatus.Pending, await ProfileStatus(db, people.Profile));
            var account = await db.Users.SingleAsync(u => u.Id == people.Sitter);
            var page = await service.PageAsync(account);
            Assert.Equal(2, page.History.Count);
            Assert.Equal(VerificationStatus.Pending, page.Status);
            Assert.Equal("The photo is blurry.", page.History.Single(h => h.Status == VerificationStatus.Rejected).DecisionReason);
        }
        finally { await Cleanup(scope.ServiceProvider, people); }
    }

    [SqlServerFact]
    public async Task Bad_uploads_are_refused_with_a_clear_message()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var (db, service) = (scope.ServiceProvider.GetRequiredService<BabySittingDbContext>(), scope.ServiceProvider.GetRequiredService<ISitterVerificationService>());
        var people = await People.CreateAsync(scope.ServiceProvider);
        try
        {
            async Task<string> Refused(Stream? file, long length, VerificationForm? form = null, Guid? user = null)
            {
                var result = await service.SubmitAsync(user ?? people.Sitter, form ?? new VerificationForm(), file, "x", length);
                Assert.False(result.Succeeded);
                return result.Message;
            }
            Assert.Contains("Choose a photo or PDF", await Refused(null, 0));
            Assert.Contains("too big", await Refused(new MemoryStream(Png), SitterVerificationService.MaxBytes + 1));
            Assert.Contains("too big", await Refused(new MemoryStream(new byte[SitterVerificationService.MaxBytes + 10]), 10));   // the real size counts, not the claimed one
            Assert.Contains("not supported", await Refused(new MemoryStream(Encoding.ASCII.GetBytes("MZ program")), 10));
            Assert.Contains("under 500", await Refused(new MemoryStream(Png), Png.Length, new VerificationForm { Note = new string('n', 501) }));
            Assert.Contains("kind of document", await Refused(new MemoryStream(Png), Png.Length, new VerificationForm { DocumentType = (VerificationDocument)99 }));
            Assert.Contains("sitter profile", await Refused(new MemoryStream(Png), Png.Length, user: people.Manager));   // no sitter profile
            Assert.False(await db.SitterVerifications.AnyAsync(v => v.SitterProfileId == people.Profile));
            Assert.Equal(VerificationStatus.NotSubmitted, await ProfileStatus(db, people.Profile));
        }
        finally { await Cleanup(scope.ServiceProvider, people); }
    }

    [SqlServerFact]
    public async Task Only_the_sitter_and_managers_can_open_the_file_and_the_database_allows_one_waiting_request()
    {
        await using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var (db, service) = (scope.ServiceProvider.GetRequiredService<BabySittingDbContext>(), scope.ServiceProvider.GetRequiredService<ISitterVerificationService>());
        var people = await People.CreateAsync(scope.ServiceProvider);
        try
        {
            Assert.True((await service.SubmitAsync(people.Sitter, new VerificationForm(), new MemoryStream(Pdf), "id.pdf", Pdf.Length)).Succeeded);
            var id = (await db.SitterVerifications.AsNoTracking().SingleAsync(v => v.SitterProfileId == people.Profile)).Id;
            Assert.Equal(Pdf, (await service.FileAsync(id, people.Sitter, manager: false))!.Bytes);
            Assert.Equal("id.pdf", (await service.FileAsync(id, people.Manager, manager: true))!.FileName);
            Assert.Null(await service.FileAsync(id, people.Manager, manager: false));        // someone else, not acting as manager
            Assert.Null(await service.FileAsync(Guid.NewGuid(), people.Sitter, manager: false));

            // Even if two submits race past the check, the filtered unique index keeps a single waiting request.
            db.SitterVerifications.Add(new SitterVerification { SitterProfileId = people.Profile, FileName = "x.pdf", ContentType = "application/pdf", Bytes = Pdf });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var report = await scope.ServiceProvider.GetRequiredService<IManagerReportService>().ReportAsync(30);
            Assert.True(report.VerificationsWaiting >= 1);
        }
        finally { await Cleanup(scope.ServiceProvider, people); }
    }

    // ----- The real pages, signed in -----

    [SqlServerFact]
    public async Task Pages_work_end_to_end_for_the_sitter_and_the_manager()
    {
        await using var factory = Factory();
        People people;
        using (var scope = factory.Services.CreateScope()) people = await People.CreateAsync(scope.ServiceProvider);
        try
        {
            using var sitter = factory.CreateClient(new() { AllowAutoRedirect = false });
            await Login(sitter, people.SitterEmail);
            var settings = await sitter.GetStringAsync("/Sitter/Settings");
            Assert.Contains("Get verified", settings);
            var page = await sitter.GetStringAsync("/Sitter/Verification");
            Assert.Contains("Send proof", page);
            Assert.Contains("Not verified yet", page);

            using var upload = new MultipartFormDataContent
            {
                { new StringContent(Hidden(page, "__RequestVerificationToken")), "__RequestVerificationToken" },
                { new StringContent("Passport"), "Form.DocumentType" },
                { new StringContent("Front page"), "Form.Note" },
            };
            var file = new ByteArrayContent(Png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            upload.Add(file, "document", "passport.png");
            var posted = await sitter.PostAsync("/Sitter/Verification", upload);
            Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
            page = await sitter.GetStringAsync("/Sitter/Verification");
            Assert.Contains("Waiting for a manager", page);
            Assert.DoesNotContain("Send for review", page);                                // the form is hidden while waiting

            // A sitter cannot open the manager page.
            var denied = await sitter.GetAsync("/SitterVerifications");
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("AccessDenied", denied.Headers.Location!.ToString());

            using var manager = factory.CreateClient(new() { AllowAutoRedirect = false });
            await Login(manager, people.ManagerEmail);
            var list = await manager.GetStringAsync("/SitterVerifications");
            Assert.Contains("Sam VerifyTest", list);
            Assert.Contains("Front page", list);
            var id = Regex.Match(list, "/SitterVerifications/Document/([0-9a-f-]{36})").Groups[1].Value;
            if (id.Length == 0) id = Regex.Match(list, "Document\\?id=([0-9a-f-]{36})").Groups[1].Value;
            Assert.NotEmpty(id);
            var document = await manager.GetAsync("/SitterVerifications/Document?id=" + id);
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            Assert.Equal("image/png", document.Content.Headers.ContentType!.MediaType);
            Assert.Equal("nosniff", document.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal(Png, await document.Content.ReadAsByteArrayAsync());

            var form = Regex.Match(list, "<form[^>]*action=\"/SitterVerifications/Approve\"[^>]*>(.*?)</form>", RegexOptions.Singleline).Groups[1].Value;
            var approve = await manager.PostAsync("/SitterVerifications/Approve", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = Hidden(form, "__RequestVerificationToken"), ["id"] = Hidden(form, "id"), ["version"] = Hidden(form, "version")
            }));
            Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
            Assert.Contains("Sitter verified", await manager.GetStringAsync("/SitterVerifications?status=verified"));

            page = await sitter.GetStringAsync("/Sitter/Verification");
            Assert.Contains("You were verified on", page);
            var own = await sitter.GetAsync("/Sitter/Verification/File/" + id);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);

            // Anonymous visitors are sent to log in.
            using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false });
            foreach (var url in new[] { "/Sitter/Verification", "/SitterVerifications", "/SitterVerifications/Document?id=" + id })
                Assert.Contains("Login", (await anonymous.GetAsync(url)).Headers.Location!.ToString());
        }
        finally { using var scope = factory.Services.CreateScope(); await Cleanup(scope.ServiceProvider, people); }
    }

    // ----- Helpers -----

    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    private static Task<VerificationStatus> ProfileStatus(BabySittingDbContext db, Guid profile) =>
        db.BabysitterProfiles.AsNoTracking().Where(p => p.Id == profile).Select(p => p.VerificationStatus).SingleAsync();

    private sealed record People(Guid Sitter, string SitterEmail, Guid Profile, Guid Manager, string ManagerEmail)
    {
        public static async Task<People> CreateAsync(IServiceProvider services)
        {
            var registration = services.GetRequiredService<RegistrationService>();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var db = services.GetRequiredService<BabySittingDbContext>();
            var key = Guid.NewGuid().ToString("N")[..12];
            var sitterEmail = $"verify-sitter-{key}@example.test";
            var (result, sitter) = await registration.RegisterAsync(sitterEmail, Password, "Sam", "VerifyTest", "Sitter");
            Assert.True(result.Succeeded);
            sitter!.EmailConfirmed = true;
            Assert.True((await users.UpdateAsync(sitter)).Succeeded);
            var profile = new BabysitterProfile { UserId = sitter.Id, Bio = "Test sitter", City = "Beirut", HourlyRate = 12, YearsOfExperience = 3 };
            db.BabysitterProfiles.Add(profile);
            await db.SaveChangesAsync();
            var managerEmail = $"verify-manager-{key}@example.test";
            var manager = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Maya", LastName = "VerifyTest", UserName = managerEmail, Email = managerEmail, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(manager, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(manager, Roles.PlatformManager)).Succeeded);
            db.ChangeTracker.Clear();
            return new(sitter.Id, sitterEmail, profile.Id, manager.Id, managerEmail);
        }
    }

    private static async Task Cleanup(IServiceProvider services, People people)
    {
        var db = services.GetRequiredService<BabySittingDbContext>();
        db.ChangeTracker.Clear();
        await db.Notifications.Where(n => n.UserId == people.Sitter || n.UserId == people.Manager || n.SourceKey!.StartsWith("verification:")
            && db.SitterVerifications.Any(v => v.SitterProfileId == people.Profile && n.SourceKey == "verification:" + v.Id)).ExecuteDeleteAsync();
        await db.SitterVerifications.Where(v => v.SitterProfileId == people.Profile).ExecuteDeleteAsync();
        await db.BabysitterProfiles.Where(p => p.Id == people.Profile).ExecuteDeleteAsync();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var email in new[] { people.SitterEmail, people.ManagerEmail })
            if (await users.FindByEmailAsync(email) is { } user) Assert.True((await users.DeleteAsync(user)).Succeeded);
    }

    private static string Hidden(string html, string name) =>
        WebUtility.HtmlDecode(Regex.Match(html, $"<input(?=[^>]*name=\"{Regex.Escape(name)}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value);

    private static async Task Login(HttpClient client, string email)
    {
        var login = await client.GetStringAsync("/Identity/Account/Login");
        var response = await client.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Hidden(login, "__RequestVerificationToken"), ["Input.Email"] = email, ["Input.Password"] = Password
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
