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

        var clientExists = await db.Clients.AnyAsync(x => x.Id == clientId, cancellationToken);
        if (!clientExists)
            throw new InvalidOperationException("The selected client no longer exists.");

        var existing = await db.ClientReferences
            .Where(x => x.ClientId == clientId)
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

            var type = IsBankOrCreditCard(accountType)
                ? ClientReferenceType.Account
                : ClientReferenceType.Category;

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
