var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var collectorApiUrl = Environment.GetEnvironmentVariable("COLLECTOR_API_URL")
    ?? "http://localhost:5000";

builder.Services.AddHttpClient("NginxCollectorApi", client =>
{
    client.BaseAddress = new Uri(collectorApiUrl);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<com.logdb.nginx.collector.ui.Layout.App>()
    .AddInteractiveServerRenderMode();

app.Run();
