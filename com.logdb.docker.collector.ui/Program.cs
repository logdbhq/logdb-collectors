var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var collectorApiUrl = Environment.GetEnvironmentVariable("COLLECTOR_API_URL")
    ?? "http://localhost:5000";

builder.Services.AddHttpClient("DockerCollectorApi", client =>
{
    client.BaseAddress = new Uri(collectorApiUrl);
});

var app = builder.Build();

var pathBase = Environment.GetEnvironmentVariable("PathBase")?.TrimEnd('/');
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<com.logdb.docker.collector.ui.Layout.App>()
    .AddInteractiveServerRenderMode();

app.Run();
