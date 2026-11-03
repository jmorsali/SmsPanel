using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;

namespace SmsPanel.Api.Services;

public sealed class MerchantImportWorker(IServiceScopeFactory scopes, IWebHostEnvironment environment, ILogger<MerchantImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using (var recoveryScope = scopes.CreateScope())
        {
            var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<SmsDbContext>();
            await recoveryDb.MerchantImportBatches.Where(x => x.Status == ImportStatus.Validating || x.Status == ImportStatus.Importing)
                .ExecuteUpdateAsync(x => x.SetProperty(b => b.Status, ImportStatus.Queued).SetProperty(b => b.CurrentStage, "بازیابی پس از راه‌اندازی مجدد"), token);
        }
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
                var mobiles = scope.ServiceProvider.GetRequiredService<MobileNormalizer>();
                var batch = await db.MerchantImportBatches.Where(x => x.Status == ImportStatus.Queued).OrderBy(x => x.Id).FirstOrDefaultAsync(token);
                if (batch is null) { await Task.Delay(TimeSpan.FromSeconds(3), token); continue; }
                await ProcessAsync(db, mobiles, batch, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "پردازش فایل پذیرندگان ناموفق بود."); await Task.Delay(TimeSpan.FromSeconds(5), token); }
        }
    }

    private async Task ProcessAsync(SmsDbContext db, MobileNormalizer mobiles, MerchantImportBatch batch, CancellationToken token)
    {
        batch.Status = ImportStatus.Validating; batch.CurrentStage = "اعتبارسنجی فایل"; batch.StartedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token);
        try
        {
            var path = Path.Combine(environment.ContentRootPath, "App_Data", "imports", batch.StoredFileName);
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheets.First();
            var headerRow = sheet.FirstRowUsed() ?? throw new InvalidDataException("فایل فاقد سطر عنوان است.");
            var headers = headerRow.CellsUsed().ToDictionary(x => Normalize(x.GetString()), x => x.Address.ColumnNumber);
            var required = new[] { "پایانه", "شناسه ملی", "نام پذیرنده" };
            var missing = required.Where(x => !headers.ContainsKey(Normalize(x))).ToArray();
            if (missing.Length > 0) throw new InvalidDataException($"ستون‌های الزامی یافت نشد: {string.Join("، ", missing)}");
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
            batch.TotalRows = Math.Max(0, lastRow - headerRow.RowNumber()); batch.Status = ImportStatus.Importing; batch.CurrentStage = "درج اطلاعات"; await db.SaveChangesAsync(token);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRow; rowNumber++)
            {
                token.ThrowIfCancellationRequested();
                var row = sheet.Row(rowNumber); var terminal = Cell(row, headers, "پایانه"); var nationalId = Cell(row, headers, "شناسه ملی"); var name = Cell(row, headers, "نام پذیرنده");
                if (string.IsNullOrWhiteSpace(terminal) || string.IsNullOrWhiteSpace(nationalId) || string.IsNullOrWhiteSpace(name)) { batch.InvalidRows++; batch.ProcessedRows++; continue; }
                if (!seen.Add(terminal) || await db.Merchants.AnyAsync(x => x.TerminalNumber == terminal, token)) { batch.DuplicateRows++; batch.ProcessedRows++; continue; }
                var rawMobile = Cell(row, headers, "موبایل");
                if (string.IsNullOrWhiteSpace(rawMobile)) rawMobile = Cell(row, headers, "شماره موبایل");
                string? mobile = null;
                if (!string.IsNullOrWhiteSpace(rawMobile)) { try { mobile = mobiles.Normalize(rawMobile); } catch (ArgumentException) { batch.InvalidRows++; } }
                var person = db.People.Local.SingleOrDefault(x => x.NationalCode == nationalId) ?? await db.People.SingleOrDefaultAsync(x => x.NationalCode == nationalId, token);
                if (person is null && mobile is not null) { person = new Person { NationalCode = nationalId, FirstName = name, LastName = string.Empty, Mobile = mobile, CurrentIban = Cell(row, headers, "شبا"), SourceImportBatchId = batch.Id }; db.People.Add(person); }
                else if (person is not null && mobile is not null) { person.Mobile = mobile; if (string.IsNullOrWhiteSpace(person.CurrentIban)) person.CurrentIban = Cell(row, headers, "شبا"); }
                db.Merchants.Add(new Merchant { ImportBatchId = batch.Id, TerminalNumber = terminal, NationalId = nationalId, MerchantName = name, Mobile = mobile, Status = Cell(row, headers, "وضعیت"), CurrentIban = Cell(row, headers, "شبا"), Bank = Cell(row, headers, "بانک"), Agency = Cell(row, headers, "نمایندگی"), PersonId = person?.Id, Person = person });
                batch.InsertedRows++; batch.ProcessedRows++;
                if (batch.ProcessedRows % 250 == 0) await db.SaveChangesAsync(token);
            }
            batch.Status = ImportStatus.Completed; batch.CurrentStage = "تکمیل‌شده"; batch.CompletedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token);
        }
        catch (Exception ex)
        {
            batch.Status = ImportStatus.Failed; batch.CurrentStage = "ناموفق"; batch.LastError = ex.Message; batch.CompletedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static string Cell(IXLRow row, IReadOnlyDictionary<string, int> headers, string title) => headers.TryGetValue(Normalize(title), out var column) ? row.Cell(column).GetFormattedString().Trim() : string.Empty;
    private static string Normalize(string value) => value.Trim().Replace("ي", "ی").Replace("ك", "ک").Replace(" ", "");
}
