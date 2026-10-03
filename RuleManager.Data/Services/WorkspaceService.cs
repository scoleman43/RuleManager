using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed class WorkspaceService(IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    private const string DefaultOrganizationName = "Default Organization";

    public async Task<Guid> GetOrCreateDefaultOrganizationIdAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Organizations
            .OrderBy(x => x.CreatedUtc)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing.HasValue)
            return existing.Value;

        var organization = new Organization
        {
            Name = DefaultOrganizationName
        };

        db.Organizations.Add(organization);
        await db.SaveChangesAsync(cancellationToken);

        return organization.Id;
    }
}
