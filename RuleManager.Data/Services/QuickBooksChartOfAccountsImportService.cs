using System.Text;
using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed record ChartOfAccountsImportResult(int Added, int Updated, int TotalRows);

public sealed class QuickBooksChartOfAccountsImportService(
    IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    public async Task<ChartOfAccountsImportResult> ImportAsync(
        Guid clientId,
        Stream csvStream,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            csvStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var rows = new List<string[]>();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            rows.Add(ParseCsvLine(line));
        }

        if (rows.Count == 0)
            throw new InvalidDataException("The Chart of Accounts CSV is empty.");

        var headers = rows[0]
            .Select((value, index) => new { value = value.Trim(), index })
            .Where(x => !string.IsNullOrWhiteSpace(x.value))
            .ToDictionary(x => x.value, x => x.index, StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[] { "Account name", "Account type", "Detail type" })
        {
            if (!headers.ContainsKey(required))
                throw new InvalidDataException($"The CSV is missing the required '{required}' column.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var client = await db.Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null)
            throw new InvalidOperationException("The selected client no longer exists.");

        var existing = await db.ClientReferences
            .Where(x => x.ClientId == clientId)
            .ToListAsync(cancellationToken);

        var organizationCategories = await db.Categories
            .Where(x => x.OrganizationId == client.OrganizationId)
            .ToListAsync(cancellationToken);

        var added = 0;
        var updated = 0;
        var totalRows = 0;

        foreach (var row in rows.Skip(1))
        {
            var name = Cell(row, headers["Account name"]);
            var accountType = Cell(row, headers["Account type"]);
            var detailType = Cell(row, headers["Detail type"]);

            if (string.IsNullOrWhiteSpace(name))
                continue;

            totalRows++;

            var isBankOrCreditCard = IsBankOrCreditCard(accountType);
            var type = isBankOrCreditCard
                ? ClientReferenceType.Account
                : ClientReferenceType.Category;

            if (!isBankOrCreditCard)
            {
                var globalCategory = organizationCategories.FirstOrDefault(x =>
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

                if (globalCategory is null)
                {
                    globalCategory = new Category
                    {
                        OrganizationId = client.OrganizationId,
                        Name = name
                    };
                    db.Categories.Add(globalCategory);
                    organizationCategories.Add(globalCategory);
                }
                else if (!globalCategory.IsActive)
                {
                    globalCategory.IsActive = true;
                    globalCategory.ModifiedUtc = DateTime.UtcNow;
                }
            }

            var match = existing.FirstOrDefault(x =>
                x.Type == type
                && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                match = new ClientReference
                {
                    ClientId = clientId,
                    Type = type,
                    Name = name,
                    QuickBooksAccountType = NullIfWhiteSpace(accountType),
                    QuickBooksDetailType = NullIfWhiteSpace(detailType),
                    Source = ClientReferenceSource.ChartOfAccountsImport,
                    IsActive = true
                };

                db.ClientReferences.Add(match);
                existing.Add(match);
                added++;
            }
            else
            {
                match.Name = name;
                match.QuickBooksAccountType = NullIfWhiteSpace(accountType);
                match.QuickBooksDetailType = NullIfWhiteSpace(detailType);
                match.Source = ClientReferenceSource.ChartOfAccountsImport;
                match.IsActive = true;
                match.ModifiedUtc = DateTime.UtcNow;
                updated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ChartOfAccountsImportResult(added, updated, totalRows);
    }

    private static bool IsBankOrCreditCard(string? accountType) =>
        string.Equals(accountType?.Trim(), "Bank", StringComparison.OrdinalIgnoreCase)
        || string.Equals(accountType?.Trim(), "Credit Card", StringComparison.OrdinalIgnoreCase);

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index].Trim() : string.Empty;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (ch == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values.ToArray();
    }
}
