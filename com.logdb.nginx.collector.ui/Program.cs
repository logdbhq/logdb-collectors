using System.Security.Claims;
using com.logdb.nginx.collector.ui.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();

var collectorApiUrl = Environment.GetEnvironmentVariable("COLLECTOR_API_URL")
    ?? "http://localhost:5000";
var apiKey = Environment.GetEnvironmentVariable("LOGDB_API_KEY");

builder.Services.AddHttpClient("NginxCollectorApi", client =>
{
    client.BaseAddress = new Uri(collectorApiUrl);
    // Present the shared key so the (now protected) collector API accepts our calls.
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});

// Single shared-password login. Auth is disabled (UI stays open) when no password is set.
var uiPassword = Environment.GetEnvironmentVariable("LOGDB_UI_PASSWORD");
var uiUsername = Environment.GetEnvironmentVariable("LOGDB_UI_USERNAME");
if (string.IsNullOrEmpty(uiUsername)) uiUsername = "admin";
var authEnabled = !string.IsNullOrEmpty(uiPassword);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "logdb_collector_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// Persist data-protection keys so the auth cookie survives container restarts.
var keyRingPath = Environment.GetEnvironmentVariable("LOGDB_DP_KEYS_DIR");
if (string.IsNullOrEmpty(keyRingPath))
    keyRingPath = Path.Combine(AppContext.BaseDirectory, "dataprotection-keys");
try
{
    Directory.CreateDirectory(keyRingPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("logdb-nginx-collector-ui");
}
catch
{
    // Fall back to default (ephemeral) keys — users simply re-login after a restart.
}

var app = builder.Build();

if (!authEnabled)
    app.Logger.LogWarning("LOGDB_UI_PASSWORD not set — operator UI is unauthenticated.");

var pathBase = Environment.GetEnvironmentVariable("PathBase")?.TrimEnd('/');
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();

// --- Auth endpoints (self-contained login form, no Blazor circuit needed) ---
app.MapGet("/login", (HttpContext http) =>
{
    var returnUrl = http.Request.Query["ReturnUrl"].ToString();
    var error = http.Request.Query.ContainsKey("error");
    return Results.Content(LoginPage.Render(returnUrl, error), "text/html");
}).AllowAnonymous();

app.MapPost("/login", async (HttpContext http) =>
{
    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var valid = authEnabled
        && UiAuth.FixedTimeEquals(username, uiUsername)
        && UiAuth.FixedTimeEquals(password, uiPassword);

    if (!valid)
    {
        var dest = "/login?error=1";
        if (!string.IsNullOrEmpty(returnUrl))
            dest += "&ReturnUrl=" + Uri.EscapeDataString(returnUrl);
        return Results.Redirect(dest);
    }

    var claims = new List<Claim> { new(ClaimTypes.Name, uiUsername) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Redirect(UiAuth.IsLocalUrl(returnUrl) ? returnUrl : "/");
}).AllowAnonymous().DisableAntiforgery();

app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous().DisableAntiforgery();

var components = app.MapRazorComponents<com.logdb.nginx.collector.ui.Layout.App>()
    .AddInteractiveServerRenderMode();
if (authEnabled)
    components.RequireAuthorization();

app.Run();
