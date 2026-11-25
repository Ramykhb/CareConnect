using CareConnect.Ai;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using BabySitting.Data;
using BabySitting.Repositories;
using BabySitting.Services.Auth;
using BabySitting.Services.Messaging;
using CareConnect.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.AddDevelopmentAiEnvironment();
builder.Services.AddControllersWithViews();
// Live notifications (WatchWise-style): SignalR hub + EF Core hook that pushes saved notifications.
builder.Services.AddSignalR();
builder.Services.Configure<CareConnect.Ai.GroqAssistantOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.PostConfigure<CareConnect.Ai.GroqAssistantOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = builder.Configuration["GROQ_API_KEY"];
});
builder.Services.AddHttpClient<CareConnect.Ai.IGroqDraftClient, CareConnect.Ai.GroqDraftClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<CareConnect.Ai.ICareSummaryClient, CareConnect.Ai.GroqCareSummaryClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<CareConnect.Ai.CareSummaryCache>();
builder.Services.AddScoped<CareConnect.Ai.CareSummaryEngine>();
builder.Services.AddScoped<CareConnect.Ai.ICareSummaryService, BabySitting.Services.CareSummaries.CareSummaryService>();
builder.Services.AddSingleton<CareConnect.Ai.AssistantThrottle>();
builder.Services.AddScoped<CareConnect.Ai.IRequestAssistantService, BabySitting.Services.RequestAssistant.RequestAssistantService>();
builder.Services.AddHostedService<BabySitting.Services.RequestAssistant.AssistantCleanupService>();
builder.Services.AddScoped<BabySitting.Services.Accounts.IAccountClosureService, BabySitting.Services.Accounts.AccountClosureService>();
builder.Services.AddScoped<BabySitting.Services.Chat.IChatService, BabySitting.Services.Chat.ChatService>();
builder.Services.AddHostedService<BabySitting.Services.Chat.MessageCleanupService>();
builder.Services.AddHostedService<BabySitting.Services.Notifications.NotificationCleanupService>();
builder.Services.AddHostedService<BabySitting.Services.Bookings.BookingExpiryService>();
builder.Services.AddHostedService<BabySitting.Services.Bookings.CareReminderService>();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddSingleton<BabySitting.Services.Notifications.LiveNotifications>();
builder.Services.AddScoped<BabySitting.Services.Portal.IPayoutReviewService, BabySitting.Services.Portal.PayoutReviewService>();
builder.Services.Configure<BabySitting.Services.Matching.AiMatchingOptions>(builder.Configuration.GetSection(BabySitting.Services.Matching.AiMatchingOptions.Section));
builder.Services.PostConfigure<BabySitting.Services.Matching.AiMatchingOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = builder.Configuration["GROQ_API_KEY"];
});
builder.Services.AddHttpClient(BabySitting.Services.Matching.SitterMatchingService.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<BabySitting.Services.Matching.ISitterMatchingService, BabySitting.Services.Matching.SitterMatchingService>();
builder.Services.AddScoped<BabySitting.Services.Portal.IManagerReportService, BabySitting.Services.Portal.ManagerReportService>();
builder.Services.AddScoped<BabySitting.Services.Portal.ISitterVerificationService, BabySitting.Services.Portal.SitterVerificationService>();
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
builder.Services.Configure<BabySitting.Services.Payments.StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.PostConfigure<BabySitting.Services.Payments.StripeOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.SecretKey)) o.SecretKey = builder.Configuration["STRIPE_SECRET_KEY"] ?? "";
    if (string.IsNullOrWhiteSpace(o.PublishableKey)) o.PublishableKey = builder.Configuration["STRIPE_PUBLISHABLE_KEY"] ?? "";
    if (string.IsNullOrWhiteSpace(o.WebhookSecret)) o.WebhookSecret = builder.Configuration["STRIPE_WEBHOOK_SECRET"] ?? "";
});
builder.Services.AddScoped<BabySitting.Services.Payments.IStripePaymentService, BabySitting.Services.Payments.StripePaymentService>();
builder.Services.AddScoped<BabySitting.Services.Payments.StripeWebhookHandler>();
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
    options.AddPolicy("ai-matching", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("care-summaries", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
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
// WebSocket handshakes are not protected by CORS; reject foreign browser origins explicitly.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hubs/request-assistant") && context.Request.Headers.Origin is { Count: > 0 } origin &&
        (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri) ||
         !string.Equals(uri.GetLeftPart(UriPartial.Authority), context.Request.Scheme + "://" + context.Request.Host, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
BabySitting.Services.Localization.UserLanguage.UseUserLanguage(app);
app.UseAuthorization();
app.MapHub<BabySitting.Services.RequestAssistant.RequestAssistantHub>(BabySitting.Services.RequestAssistant.RequestAssistantHub.Path);
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
