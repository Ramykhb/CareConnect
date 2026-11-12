using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using BabySitting.Data;
using BabySitting.Models;

namespace BabySitting.Services.Auth;

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
        var protection = services.AddDataProtection().SetApplicationName("CareConnect.BabySitting");
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
            .AddEntityFrameworkStores<BabySittingDbContext>()
            .AddSignInManager<ActiveUserSignInManager>();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "CareConnect.BabySitting.Identity";
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
                options.Cookie.Name = "CareConnect.BabySitting." + scheme;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            });
        services.AddAntiforgery(options => options.Cookie.Name = "CareConnect.BabySitting.Antiforgery");
        services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(options => options.Cookie.Name = "CareConnect.BabySitting.TempData");
        // Password resets, role changes, and account deactivation take effect on the next request.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(2));
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection("Email"))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && MailAddressIsValid(options.From)
                && options.Port is > 0 and <= 65535, "Configure Email:Host, Email:Port and Email:From.")
            .Validate(options => environment.IsDevelopment() || options.EnableSsl, "SMTP TLS is required outside Development.")
            .ValidateOnStart();
        services.AddTransient<IEmailSender, SmtpEmailSender>();
        services.AddScoped<RegistrationService>();
        services.AddRazorPages(options => options.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));
        return services;
    }

    private static bool MailAddressIsValid(string value) => System.Net.Mail.MailAddress.TryCreate(value, out _);
}

public sealed class AuthOriginOptions
{
    public string PublicOrigin { get; set; } = string.Empty;
}
