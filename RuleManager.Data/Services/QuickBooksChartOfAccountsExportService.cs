using System.Text;
using Microsoft.EntityFrameworkCore;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed record QuickBooksChartOfAccountsExportResult(
    byte[] Content,
    string FileName,
    int AccountCount);

public sealed class QuickBooksChartOfAccountsExportService(
    IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    public async Task<QuickBooksChartOfAccountsExportResult> GenerateAsync(
        Guid clientId,
        string? outputClientName = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var client = await db.Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken)
            ?? throw new InvalidOperationException("The selected client no longer exists.");

        var references = await db.ClientReferences
            .Where(x => x.ClientId == clientId
                && x.IsActive
                && x.Source == ClientReferenceSource.ChartOfAccountsImport
                && (x.Type == ClientReferenceType.Account
                    || x.Type == ClientReferenceType.Category))
            .OrderBy(x => x.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (references.Count == 0)
            throw new InvalidOperationException(
                "This client does not have an imported QuickBooks Chart of Accounts to export.");

        var csv = new StringBuilder();
        csv.AppendLine("Account Number,Account Name,Type,Detail Type");

        foreach (var reference in references)
        {
            csv.Append(Csv(reference.QuickBooksAccountNumber));
            csv.Append(',');
            csv.Append(Csv(reference.Name));
            csv.Append(',');
            csv.Append(Csv(reference.QuickBooksAccountType));
            csv.Append(',');
            csv.Append(Csv(reference.QuickBooksDetailType));
            csv.AppendLine();
        }

        var safeName = SanitizeFileName(
            string.IsNullOrWhiteSpace(outputClientName) ? client.Name : outputClientName);
        return new QuickBooksChartOfAccountsExportResult(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString()),
            $"{safeName}_Chart_of_Accounts_QBO_Import.csv",
            references.Count);
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) ? "QuickBooks" : name.Trim();
    }
}
