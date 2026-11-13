using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using BabySitting.Data;
using BabySitting.Repositories;
using BabySitting.Services.Auth;
using BabySitting.Services.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<BabySittingDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.EnableRetryOnFailure()));
// Scoped (one per HTTP request) because it uses the scoped DbContext.
builder.Services.AddScoped<IFamilyRepository, FamilyRepository>();
builder.Services.AddScoped<ICareRequestRepository, CareRequestRepository>();
builder.Services.AddScoped<BabySitting.Services.Bookings.IBookingCancellationService, BabySitting.Services.Bookings.BookingCancellationService>();
builder.Services.AddScoped<BabySitting.Services.Portal.ISitterReportingService, BabySitting.Services.Portal.SitterReportingService>();
builder.Services.AddScoped<BabySitting.Services.Portal.ISitterAccountService, BabySitting.Services.Portal.SitterAccountService>();
builder.Services.AddScoped<BabySitting.Services.Bookings.ISitterBookingService, BabySitting.Services.Bookings.SitterBookingService>();
builder.Services.AddScoped<BabySitting.Services.Scheduling.ISitterAvailabilityService, BabySitting.Services.Scheduling.SitterAvailabilityService>();
builder.Services.AddScoped<BabySitting.Services.Requests.ISitterRequestService, BabySitting.Services.Requests.SitterRequestService>();
builder.Services.AddScoped<BabySitting.Services.Dashboard.IDashboardService, BabySitting.Services.Dashboard.DashboardService>();
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/Identity") && HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("public"));
});
builder.Services.AddCareConnectIdentity(builder.Configuration, builder.Environment);

var app = builder.Build();
if (args.Contains("--initialize-identity"))
{
    await IdentitySetup.InitializeAsync(app.Services, app.Configuration);
    return;
}
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
// Canonical origin is used by Identity when generating confirmation and recovery links.
app.Use(async (context, next) =>
{
    var origin = new Uri(context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOriginOptions>>().Value.PublicOrigin);
    context.Request.Scheme = origin.Scheme;
    context.Request.Host = new HostString(origin.Host, origin.Port);
    await next(context);
});
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/Identity") || context.Request.Path.StartsWithSegments("/Dashboard"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapStaticAssets();
app.MapHealthChecks("/health/live");
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();
app.Run();

// Exposes the entry point to application smoke tests.
public partial class Program { }
