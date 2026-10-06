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

        // Temporary development schema bridge until the project switches fully to EF migrations.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientRuleAssignments" ADD COLUMN IF NOT EXISTS "ExportPriority" integer NOT NULL DEFAULT 0;""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientRules" ADD COLUMN IF NOT EXISTS "ExportPriority" integer NOT NULL DEFAULT 0;""",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "MasterRules" ADD COLUMN IF NOT EXISTS "ImportedQuickBooksRuleName" character varying(255);""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "MasterRules" ADD COLUMN IF NOT EXISTS "IsModifiedSinceImport" boolean NOT NULL DEFAULT FALSE;""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientRules" ADD COLUMN IF NOT EXISTS "ImportedQuickBooksRuleName" character varying(255);""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientRules" ADD COLUMN IF NOT EXISTS "IsModifiedSinceImport" boolean NOT NULL DEFAULT FALSE;""",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "MasterRules"
            SET "ImportedQuickBooksRuleName" = "Name"
            WHERE "ImportedQuickBooksRuleName" IS NULL
              AND ("OriginalConditionsJson" IS NOT NULL OR "OriginalOutputsJson" IS NOT NULL);
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "ClientRules"
            SET "ImportedQuickBooksRuleName" = COALESCE("SourceImportKey", "Name")
            WHERE "ImportedQuickBooksRuleName" IS NULL
              AND ("SourceImportKey" IS NOT NULL OR "OriginalConditionsJson" IS NOT NULL OR "OriginalOutputsJson" IS NOT NULL);
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ClientReferences" (
                "Id" uuid NOT NULL,
                "CreatedUtc" timestamp with time zone NOT NULL,
                "ModifiedUtc" timestamp with time zone NOT NULL,
                "ClientId" uuid NOT NULL,
                "Type" integer NOT NULL,
                "Name" character varying(255) NOT NULL,
                "QuickBooksAccountType" character varying(100),
                "QuickBooksDetailType" character varying(150),
                "Source" integer NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT TRUE,
                CONSTRAINT "PK_ClientReferences" PRIMARY KEY ("Id")
            );
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClientReferences_ClientId_Type_Name"
            ON "ClientReferences" ("ClientId", "Type", "Name");
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientReferences" ADD COLUMN IF NOT EXISTS "QuickBooksAccountType" character varying(100);""",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientReferences" ADD COLUMN IF NOT EXISTS "QuickBooksDetailType" character varying(150);""",
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            WITH ranked AS (
                SELECT "Id",
                       ROW_NUMBER() OVER (PARTITION BY "ClientId" ORDER BY "CreatedUtc", "Id") AS priority
                FROM "ClientRuleAssignments"
                WHERE "ExportPriority" = 0
            )
            UPDATE "ClientRuleAssignments" AS a
            SET "ExportPriority" = ranked.priority
            FROM ranked
            WHERE a."Id" = ranked."Id";
            """,
            cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            WITH base AS (
                SELECT "ClientId", COALESCE(MAX("ExportPriority"), 0) AS max_priority
                FROM "ClientRuleAssignments"
                GROUP BY "ClientId"
            ),
            ranked AS (
                SELECT r."Id",
                       COALESCE(base.max_priority, 0)
                       + ROW_NUMBER() OVER (PARTITION BY r."ClientId" ORDER BY r."CreatedUtc", r."Id") AS priority
                FROM "ClientRules" r
                LEFT JOIN base ON base."ClientId" = r."ClientId"
                WHERE r."ExportPriority" = 0
            )
            UPDATE "ClientRules" AS r
            SET "ExportPriority" = ranked.priority
            FROM ranked
            WHERE r."Id" = ranked."Id";
            """,
            cancellationToken);

        var workspace = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
        await workspace.GetOrCreateDefaultOrganizationIdAsync(cancellationToken);
    }
}
