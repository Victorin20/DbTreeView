using DbTreeView.Web.Components;
using DbTreeView.Web.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var keyDirectory = new DirectoryInfo(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys"));

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(keyDirectory)
    .SetApplicationName("DB Tree View");

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

// A scoped session belongs to one Blazor circuit. Credentials are kept in
// server memory only and disappear when the circuit is closed.
builder.Services.AddScoped<DatabaseSession>();
builder.Services.AddScoped<SqlServerMetadataService>();
builder.Services.AddScoped<SqlServerPerformanceService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
