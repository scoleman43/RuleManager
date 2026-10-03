using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RuleManager.Data.Services;

namespace RuleManager.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RuleManagerDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        await db.Database.EnsureCreatedAsync(cancellationToken);

        var workspace = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        await workspace.GetOrCreateDefaultOrganizationIdAsync(cancellationToken);
    }
}
