using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using BabySitting.Data;
using BabySitting.Repositories;
using BabySitting.Services.Auth;
using BabySitting.Services.Messaging;
using CareConnect.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
// Live notifications (WatchWise-style): SignalR hub + EF Core hook that pushes saved notifications.
builder.Services.AddSignalR();
builder.Services.AddScoped<BabySitting.Services.Accounts.IAccountClosureService, BabySitting.Services.Accounts.AccountClosureService>();
builder.Services.AddScoped<BabySitting.Services.Chat.IChatService, BabySitting.Services.Chat.ChatService>();
builder.Services.AddHostedService<BabySitting.Services.Chat.MessageCleanupService>();
builder.Services.AddHostedService<BabySitting.Services.Notifications.NotificationCleanupService>();
builder.Services.AddHostedService<BabySitting.Services.Bookings.BookingExpiryService>();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddSingleton<BabySitting.Services.Notifications.LiveNotifications>();
builder.Services.AddScoped<BabySitting.Services.Portal.IPayoutReviewService, BabySitting.Services.Portal.PayoutReviewService>();
builder.Services.AddScoped<BabySitting.Services.Accounts.IManagerUserService, BabySitting.Services.Accounts.ManagerUserService>();
builder.Services.AddScoped<BabySitting.Services.Requests.IOfferChoiceService, BabySitting.Services.Requests.OfferChoiceService>();
builder.Services.AddSingleton<BabySitting.Services.Notifications.NotificationEmailQueue>();
builder.Services.AddSingleton<BabySitting.Services.Notifications.NotificationEmailWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BabySitting.Services.Notifications.NotificationEmailWorker>());
builder.Services.AddSingleton<BabySitting.Services.Notifications.NotificationBroadcastInterceptor>();
builder.Services.AddScoped<BabySitting.Services.Notifications.INotificationFeedService, BabySitting.Services.Notifications.NotificationFeedService>();
builder.Services.AddDbContext<BabySittingDbContext>((services, options) =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.EnableRetryOnFailure())
        .AddInterceptors(services.GetRequiredService<BabySitting.Services.Notifications.NotificationBroadcastInterceptor>()));
// Scoped (one per HTTP request) because it uses the scoped DbContext.
builder.Services.AddScoped<IFamilyRepository, FamilyRepository>();
builder.Services.AddScoped<ICareRequestRepository, CareRequestRepository>();
builder.Services.AddScoped<BabySitting.Services.Bookings.ParentBookingService>();
builder.Services.AddScoped<BabySitting.Services.Bookings.IParentPaymentService, BabySitting.Services.Bookings.ParentPaymentService>();
builder.Services.AddScoped<BabySitting.Services.Bookings.IBookingCancellationService, BabySitting.Services.Bookings.BookingCancellationService>();
builder.Services.AddScoped<BabySitting.Services.Portal.ISitterReportingService, BabySitting.Services.Portal.SitterReportingService>();
builder.Services.AddScoped<BabySitting.Services.Portal.ISitterAccountService, BabySitting.Services.Portal.SitterAccountService>();
builder.Services.AddScoped<BabySitting.Services.Bookings.ISitterBookingService, BabySitting.Services.Bookings.SitterBookingService>();
builder.Services.AddScoped<BabySitting.Services.Scheduling.ISitterAvailabilityService, BabySitting.Services.Scheduling.SitterAvailabilityService>();
builder.Services.AddScoped<BabySitting.Services.Requests.ISitterRequestService, BabySitting.Services.Requests.SitterRequestService>();
builder.Services.AddScoped<BabySitting.Services.Dashboard.IDashboardService, BabySitting.Services.Dashboard.DashboardService>();
builder.Services.AddOptions<BrokerOptions>().Bind(builder.Configuration.GetSection("RabbitMq"))
    .PostConfigure(o => { o.Platform = "BabySitting"; o.Queue = "babysitting.events"; })
    .Validate(o => !o.Enabled || (!string.IsNullOrWhiteSpace(o.HostName) && o.Port is > 0 and <= 65535 &&
        !string.IsNullOrWhiteSpace(o.UserName) && !string.IsNullOrWhiteSpace(o.Password) && !string.IsNullOrWhiteSpace(o.Exchange)),
        "Enabled RabbitMQ requires host, port, exchange and credentials.").ValidateOnStart();
builder.Services.AddScoped<IIntegrationStore, IntegrationStore>();
builder.Services.AddHostedService<RabbitMqWorker>();
builder.Services.AddSingleton<BrokerHealth>();
builder.Services.AddHealthChecks().AddCheck<BrokerHealth>("rabbitmq", tags: ["ready"]);
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
if (args.Contains("--replay-dead-letters"))
{
    var count = await DeadLetterReplay.RunAsync(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BrokerOptions>>().Value);
    Console.WriteLine($"Replayed {count} dead-letter message(s).");
    return;
}
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
BabySitting.Services.Localization.UserLanguage.UseUserLanguage(app);
app.UseAuthorization();
app.MapRazorPages();
app.MapStaticAssets();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapHub<BabySitting.Services.Chat.ChatHub>(BabySitting.Services.Chat.ChatHub.Path);
app.MapHub<BabySitting.Services.Notifications.NotificationHub>(BabySitting.Services.Notifications.NotificationHub.Path);
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();
app.Run();

// Exposes the entry point to application smoke tests.
public partial class Program { }
