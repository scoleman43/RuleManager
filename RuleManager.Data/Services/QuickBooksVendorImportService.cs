using Microsoft.EntityFrameworkCore;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using RuleManager.Core.Domain;

namespace RuleManager.Data.Services;

public sealed record VendorImportResult(int Added, int Updated, int TotalRows);

public sealed class QuickBooksVendorImportService(
    IDbContextFactory<RuleManagerDbContext> dbFactory)
{
    public async Task<VendorImportResult> ImportAsync(
        Guid clientId,
        Stream workbookStream,
        CancellationToken cancellationToken = default)
    {
        using var workbook = new HSSFWorkbook(workbookStream);

        if (workbook.NumberOfSheets == 0)
            throw new InvalidDataException("The vendor workbook does not contain a worksheet.");

        var sheet = workbook.GetSheetAt(0);
        var formatter = new DataFormatter();

        var headerRow = sheet.GetRow(sheet.FirstRowNum)
            ?? throw new InvalidDataException("The vendor workbook does not contain a header row.");

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = headerRow.FirstCellNum; i < headerRow.LastCellNum; i++)
        {
            var value = formatter.FormatCellValue(headerRow.GetCell(i)).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                headers[value] = i;
        }

        if (!headers.TryGetValue("Vendor", out var vendorColumn))
            throw new InvalidDataException("The workbook is missing the required 'Vendor' column.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var clientExists = await db.Clients.AnyAsync(x => x.Id == clientId, cancellationToken);
        if (!clientExists)
            throw new InvalidOperationException("The selected client no longer exists.");

        var existing = await db.ClientReferences
            .Where(x => x.ClientId == clientId && x.Type == ClientReferenceType.Payee)
            .ToListAsync(cancellationToken);

        var added = 0;
        var updated = 0;
        var totalRows = 0;

        for (var rowIndex = sheet.FirstRowNum + 1; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null)
                continue;

            var vendorName = formatter.FormatCellValue(row.GetCell(vendorColumn)).Trim();
            if (string.IsNullOrWhiteSpace(vendorName))
                continue;

            totalRows++;

            var match = existing.FirstOrDefault(x =>
                string.Equals(x.Name, vendorName, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                match = new ClientReference
                {
                    ClientId = clientId,
                    Type = ClientReferenceType.Payee,
                    Name = vendorName,
                    Source = ClientReferenceSource.VendorImport,
                    IsActive = true
                };

                db.ClientReferences.Add(match);
                existing.Add(match);
                added++;
            }
            else
            {
                match.Name = vendorName;
                match.Source = ClientReferenceSource.VendorImport;
                match.IsActive = true;
                match.ModifiedUtc = DateTime.UtcNow;
                updated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new VendorImportResult(added, updated, totalRows);
    }
}
