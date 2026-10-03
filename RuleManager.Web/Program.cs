using Microsoft.EntityFrameworkCore;
using RuleManager.Data;
using RuleManager.Data.Services;
using RuleManager.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("RuleManager")
    ?? throw new InvalidOperationException("Connection string 'RuleManager' is not configured.");

builder.Services.AddDbContextFactory<RuleManagerDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<RuleSetAssignmentService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
else
{
    await DatabaseInitializer.InitializeAsync(app.Services);
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
