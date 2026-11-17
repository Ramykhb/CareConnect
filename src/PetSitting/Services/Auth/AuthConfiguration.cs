using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using PetSitting.Data;
using PetSitting.Models;

namespace PetSitting.Services.Auth;

public static class AuthConfiguration
{
    public static IServiceCollection AddCareConnectIdentity(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddOptions<AuthOriginOptions>().Bind(configuration.GetSection("Auth"))
            .Validate(options => Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || (environment.IsDevelopment() && uri.Scheme == "http"))
                && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
                "Set Auth:PublicOrigin to this application's public origin (HTTPS outside Development).")
            .ValidateOnStart();
        var protection = services.AddDataProtection().SetApplicationName("CareConnect.PetSitting");
        if (configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
            protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedAccount = true;
            options.Password.RequiredLength = 12;
            options.Password.RequiredUniqueChars = 4;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<PetSittingDbContext>()
            .AddSignInManager<ActiveUserSignInManager>();
        // Leave password sign-in available when Google credentials have not been configured.
        var googleId = configuration["Authentication:Google:ClientId"];
        var googleSecret = configuration["Authentication:Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(googleId) != string.IsNullOrWhiteSpace(googleSecret))
            throw new InvalidOperationException("Configure both Authentication:Google:ClientId and ClientSecret, or neither.");
        if (!string.IsNullOrWhiteSpace(googleId))
            services.AddAuthentication().AddGoogle(options =>
            {
                options.ClientId = googleId;
                options.ClientSecret = googleSecret!;
                options.CallbackPath = "/signin-google";
                options.CorrelationCookie.Name = "CareConnect.PetSitting.Google.Correlation.";
                options.ClaimActions.MapJsonKey("google:email_verified", "email_verified");
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/Identity/Account/Login?googleError=true");
                    return Task.CompletedTask;
                };
            });
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "CareConnect.PetSitting.Identity";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = async context =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(context);
                if (context.Principal?.Identity?.IsAuthenticated != true) return;
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.GetUserAsync(context.Principal);
                if (user is null || !user.IsActive)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>().SignOutAsync();
                }
            };
        });
        foreach (var scheme in new[] { IdentityConstants.ExternalScheme, IdentityConstants.TwoFactorRememberMeScheme, IdentityConstants.TwoFactorUserIdScheme })
            services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(scheme, options =>
            {
                options.Cookie.Name = "CareConnect.PetSitting." + scheme;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            });
        services.AddAntiforgery(options => options.Cookie.Name = "CareConnect.PetSitting.Antiforgery");
        services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(options => options.Cookie.Name = "CareConnect.PetSitting.TempData");
        // Password resets, role changes, and account deactivation take effect on the next request.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(2));
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection("Email"))
            .PostConfigure(options =>
            {
                if (string.Equals(options.Provider, "Brevo", StringComparison.OrdinalIgnoreCase))
                {
                    options.Host = "smtp-relay.brevo.com";
                    options.Port = 587;
                    options.EnableSsl = true; // STARTTLS with certificate validation.
                }
            })
            .Validate(options => options.Provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
                || options.Provider.Equals("Brevo", StringComparison.OrdinalIgnoreCase), "Email:Provider must be Smtp or Brevo.")
            .Validate(options => !options.Provider.Equals("Brevo", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(options.UserName) && !string.IsNullOrWhiteSpace(options.Password)
                    && !options.Password.StartsWith("xkeysib-", StringComparison.OrdinalIgnoreCase)),
                "Brevo requires Email:UserName (SMTP login), Email:Password (SMTP key, not API key), and Email:From (verified sender).")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && MailAddressIsValid(options.From)
                && options.Port is > 0 and <= 65535, "Configure Email:Host, Email:Port and Email:From.")
            .Validate(options => environment.IsDevelopment() || options.EnableSsl, "SMTP TLS is required outside Development.")
            .ValidateOnStart();
        services.AddTransient<IEmailSender, SmtpEmailSender>();
        services.AddScoped<RegistrationService>();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<PasswordResetCooldown>();
        services.AddScoped<PasswordResetService>();
        services.AddRazorPages(options => options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
        return services;
    }

    private static bool MailAddressIsValid(string value) => System.Net.Mail.MailAddress.TryCreate(value, out _);
}

public sealed class AuthOriginOptions
{
    public string PublicOrigin { get; set; } = string.Empty;
}
