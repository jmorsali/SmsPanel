using System.Xml;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;

namespace SmsPanel.Api.Services;

public sealed class MerchantCampaignRecipients(SmsDbContext db, IWebHostEnvironment environment)
{
    private static void ReadNationalIds(Stream stream, HashSet<string> nationalIds, CancellationToken token)
    {
        try
        {
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.FirstOrDefault();
            var header = sheet?.FirstRowUsed();
            if (header is null)
                throw new InvalidDataException("فایل فاقد سطر عنوان است.");

            var columns = header.CellsUsed().Select(cell => new
            {
                Title = string.Concat(cell.GetString().Where(c => !char.IsWhiteSpace(c) && c != '\u200c'))
                    .Replace('ي', 'ی').Replace('ك', 'ک'),
                Number = cell.Address.ColumnNumber
            }).ToList();
            var column = columns.FirstOrDefault(x => x.Title == "شناسهملی")
                ?? columns.FirstOrDefault(x => x.Title is "کدملی" or "شمارهملی");
            if (column is null)
                throw new InvalidDataException("ستون «شناسه ملی» یا «کد ملی» در اولین برگهٔ فایل یافت نشد.");

            foreach (var cell in sheet!.Column(column.Number).CellsUsed())
            {
                token.ThrowIfCancellationRequested();
                if (cell.Address.RowNumber <= header.RowNumber()) continue;
                var id = cell.GetFormattedString().Trim();
                if (id.Length > 0) nationalIds.Add(id);
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException
            && ex is OpenXmlPackageException or XmlException or IOException
                or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new InvalidDataException("فایل اکسل قابل خواندن نیست.", ex);
        }
    }

    public async Task<MerchantCampaignRecipientSelection> FindAsync(IReadOnlyList<long>? importBatchIds, CancellationToken token)
    {
        var batchIds = importBatchIds?.Distinct().ToArray() ?? [];
        if (batchIds.Length == 0)
            throw new InvalidDataException("حداقل یک فایل پذیرندگان را انتخاب کنید.");

        var batches = await db.MerchantImportBatches.AsNoTracking()
            .Where(batch => EF.Parameter(batchIds).Contains(batch.Id)
                && batch.Status == ImportStatus.Completed && batch.StoredFileName != "manual")
            .Select(batch => batch.StoredFileName)
            .ToListAsync(token);
        if (batches.Count != batchIds.Length)
            throw new InvalidDataException("یک یا چند فایل انتخاب‌شده در فهرست فایل‌های تکمیل‌شده موجود نیست.");

        var nationalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileName in batches)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(environment.ContentRootPath, "App_Data", "imports", fileName);
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                ReadNationalIds(stream, nationalIds, token);
            }
            catch (IOException)
            {
                throw new InvalidDataException("دسترسی به یک یا چند فایل انتخاب‌شده امکان‌پذیر نیست.");
            }
        }

        var ids = nationalIds.ToArray();
        if (ids.Length == 0)
            return new MerchantCampaignRecipientSelection([], 0, 0, 0, 0);

        var matches = await db.People.AsNoTracking()
            .Where(person => EF.Parameter(ids).Contains(person.NationalCode))
            .Select(person => new
            {
                Person = person,
                HasBackupIban = db.SecondaryBankAccounts.Any(account => account.PersonId == person.Id)
            })
            .ToListAsync(token);

        var withoutBackupIban = matches.Where(match => !match.HasBackupIban).ToList();
        var eligible = withoutBackupIban.Select(match => match.Person)
            .Where(person => person.Mobile.Length == 11 && person.Mobile.StartsWith("09", StringComparison.Ordinal)
                && person.Mobile.All(char.IsAsciiDigit))
            .ToList();

        return new MerchantCampaignRecipientSelection(eligible, ids.Length, ids.Length - matches.Count,
            matches.Count - withoutBackupIban.Count, withoutBackupIban.Count - eligible.Count);
    }
}

public sealed record MerchantCampaignRecipientSelection(
    IReadOnlyList<Person> People, int UniqueIdentifierCount, int UnmatchedCount, int WithBackupIbanCount, int WithoutMobileCount);
