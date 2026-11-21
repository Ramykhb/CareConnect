using CareConnect.Ai;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PetSitting.Data;
using PetSitting.Repositories;
using PetSitting.Services.Auth;
using PetSitting.Services.Messaging;
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
builder.Services.AddSingleton<CareConnect.Ai.AssistantThrottle>();
builder.Services.AddScoped<CareConnect.Ai.IRequestAssistantService, PetSitting.Services.RequestAssistant.RequestAssistantService>();
builder.Services.AddHostedService<PetSitting.Services.RequestAssistant.AssistantCleanupService>();
builder.Services.AddScoped<PetSitting.Services.Accounts.IAccountClosureService, PetSitting.Services.Accounts.AccountClosureService>();
builder.Services.AddScoped<PetSitting.Services.Chat.IChatService, PetSitting.Services.Chat.ChatService>();
builder.Services.AddHostedService<PetSitting.Services.Chat.MessageCleanupService>();
builder.Services.AddHostedService<PetSitting.Services.Notifications.NotificationCleanupService>();
builder.Services.AddHostedService<PetSitting.Services.Bookings.BookingExpiryService>();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddSingleton<PetSitting.Services.Notifications.LiveNotifications>();
builder.Services.AddScoped<PetSitting.Services.Portal.IPayoutReviewService, PetSitting.Services.Portal.PayoutReviewService>();
builder.Services.Configure<PetSitting.Services.Matching.AiMatchingOptions>(builder.Configuration.GetSection(PetSitting.Services.Matching.AiMatchingOptions.Section));
builder.Services.PostConfigure<PetSitting.Services.Matching.AiMatchingOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = builder.Configuration["GROQ_API_KEY"];
});
builder.Services.AddHttpClient(PetSitting.Services.Matching.SitterMatchingService.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<PetSitting.Services.Matching.ISitterMatchingService, PetSitting.Services.Matching.SitterMatchingService>();
builder.Services.AddScoped<PetSitting.Services.Portal.IManagerReportService, PetSitting.Services.Portal.ManagerReportService>();
builder.Services.AddScoped<PetSitting.Services.Portal.ISitterVerificationService, PetSitting.Services.Portal.SitterVerificationService>();
builder.Services.AddScoped<PetSitting.Services.Accounts.IManagerUserService, PetSitting.Services.Accounts.ManagerUserService>();
builder.Services.AddScoped<PetSitting.Services.Requests.IOfferChoiceService, PetSitting.Services.Requests.OfferChoiceService>();
builder.Services.AddSingleton<PetSitting.Services.Notifications.NotificationEmailQueue>();
builder.Services.AddSingleton<PetSitting.Services.Notifications.NotificationEmailWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PetSitting.Services.Notifications.NotificationEmailWorker>());
builder.Services.AddSingleton<PetSitting.Services.Notifications.NotificationBroadcastInterceptor>();
builder.Services.AddScoped<PetSitting.Services.Notifications.INotificationFeedService, PetSitting.Services.Notifications.NotificationFeedService>();
builder.Services.AddScoped<PetSitting.Services.Bookings.OwnerBookingService>();
builder.Services.AddScoped<PetSitting.Services.Portal.ISitterReportingService, PetSitting.Services.Portal.SitterReportingService>();
builder.Services.AddScoped<PetSitting.Services.Portal.ISitterAccountService, PetSitting.Services.Portal.SitterAccountService>();
builder.Services.AddScoped<PetSitting.Services.Bookings.IBookingCancellationService, PetSitting.Services.Bookings.BookingCancellationService>();
builder.Services.AddScoped<PetSitting.Services.Bookings.ISitterBookingService, PetSitting.Services.Bookings.SitterBookingService>();
builder.Services.AddScoped<PetSitting.Services.Scheduling.ISitterAvailabilityService, PetSitting.Services.Scheduling.SitterAvailabilityService>();
builder.Services.AddScoped<PetSitting.Services.Requests.ISitterRequestService, PetSitting.Services.Requests.SitterRequestService>();
builder.Services.AddScoped<PetSitting.Services.Dashboard.IDashboardService, PetSitting.Services.Dashboard.DashboardService>();
builder.Services.AddDbContext<PetSittingDbContext>((services, options) =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.EnableRetryOnFailure())
        .AddInterceptors(services.GetRequiredService<PetSitting.Services.Notifications.NotificationBroadcastInterceptor>()));
// Scoped (one per HTTP request) because it uses the scoped DbContext.
builder.Services.AddScoped<IPetRepository, PetRepository>();
builder.Services.AddScoped<PetCareRequestRepository>();
builder.Services.AddOptions<BrokerOptions>().Bind(builder.Configuration.GetSection("RabbitMq"))
    .PostConfigure(o => { o.Platform = "PetSitting"; o.Queue = "petsitting.events"; })
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
PetSitting.Services.Localization.UserLanguage.UseUserLanguage(app);
app.UseAuthorization();
app.MapHub<PetSitting.Services.RequestAssistant.RequestAssistantHub>(PetSitting.Services.RequestAssistant.RequestAssistantHub.Path);
app.MapRazorPages();
app.MapStaticAssets();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapHub<PetSitting.Services.Chat.ChatHub>(PetSitting.Services.Chat.ChatHub.Path);
app.MapHub<PetSitting.Services.Notifications.NotificationHub>(PetSitting.Services.Notifications.NotificationHub.Path);
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();
app.Run();

// Exposes the entry point to application smoke tests.
public partial class Program { }
