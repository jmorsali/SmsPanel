using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using ClosedXML.Excel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using System.Text.Json.Serialization;
using SmsPanel.Api.Data;
using SmsPanel.Api.Domain;
using SmsPanel.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<FarapayamakOptions>(builder.Configuration.GetSection("Farapayamak"));
builder.Services.AddDbContext<SmsDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("SmsDatabase"), s => s.EnableRetryOnFailure()));
builder.Services.AddHttpClient<IFarapayamakClient, FarapayamakClient>();
builder.Services.AddSingleton<IbanService>();
builder.Services.AddSingleton<MobileNormalizer>();
builder.Services.AddSingleton<MessageTemplateRenderer>();
builder.Services.AddScoped<InboundMessageProcessor>();
builder.Services.AddHostedService<InboxPollingWorker>();
builder.Services.AddHostedService<OutboundDispatchWorker>();
builder.Services.AddHostedService<MerchantImportWorker>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => { o.Cookie.Name = "SmsPanel.Auth"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; o.SlidingExpiration = true; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }; o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }; });
builder.Services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 100 * 1024 * 1024);
var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/api/auth/status", async (ClaimsPrincipal principal, SmsDbContext db) => Results.Ok(new { authenticated = principal.Identity?.IsAuthenticated == true, needsSetup = !await db.AppUsers.AnyAsync(), user = principal.Identity?.IsAuthenticated == true ? new { username = principal.Identity.Name, displayName = principal.FindFirstValue("display_name"), isAdmin = principal.IsInRole("Admin") } : null })).AllowAnonymous();
app.MapPost("/api/auth/setup", async (RequestCreateUser request, SmsDbContext db, IPasswordHasher<AppUser> hasher, HttpContext http) => { if (await db.AppUsers.AnyAsync()) return Results.Conflict(new { message = "راه‌اندازی اولیه قبلاً انجام شده است." }); var error = ValidateUser(request); if (error is not null) return Results.BadRequest(new { message = error }); var user = new AppUser { Username = request.Username.Trim(), DisplayName = request.DisplayName.Trim(), PasswordHash = "", IsAdmin = true }; user.PasswordHash = hasher.HashPassword(user, request.Password); db.Add(user); await db.SaveChangesAsync(); await SignIn(http, user); return Results.Ok(new { user.Username, user.DisplayName, user.IsAdmin }); }).AllowAnonymous();
app.MapPost("/api/auth/login", async (RequestLogin request, SmsDbContext db, IPasswordHasher<AppUser> hasher, HttpContext http) => { var normalized = request.Username.Trim(); var user = await db.AppUsers.SingleOrDefaultAsync(x => x.Username == normalized); if (user is null || !user.IsActive || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed) return Results.Unauthorized(); user.LastLoginAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); await SignIn(http, user); return Results.Ok(new { user.Username, user.DisplayName, user.IsAdmin }); }).AllowAnonymous();
app.MapPost("/api/auth/logout", async (HttpContext http) => { await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.NoContent(); });
app.MapGet("/api/users", async (SmsDbContext db) => Results.Ok(await db.AppUsers.AsNoTracking().OrderBy(x => x.Username).Select(x => new { x.Id, x.Username, x.DisplayName, x.IsAdmin, x.IsActive, x.CreatedAtUtc, x.LastLoginAtUtc }).ToListAsync())).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapPost("/api/users", async (RequestCreateUser request, SmsDbContext db, IPasswordHasher<AppUser> hasher) => { var error = ValidateUser(request); if (error is not null) return Results.BadRequest(new { message = error }); var username = request.Username.Trim(); if (await db.AppUsers.AnyAsync(x => x.Username == username)) return Results.Conflict(new { message = "این نام کاربری قبلاً ثبت شده است." }); var user = new AppUser { Username = username, DisplayName = request.DisplayName.Trim(), PasswordHash = "", IsAdmin = request.IsAdmin }; user.PasswordHash = hasher.HashPassword(user, request.Password); db.Add(user); await db.SaveChangesAsync(); return Results.Created($"/api/users/{user.Id}", new { user.Id }); }).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapPatch("/api/users/{id:long}/status", async (long id, RequestUserStatus request, ClaimsPrincipal principal, SmsDbContext db) => { var user = await db.AppUsers.FindAsync(id); if (user is null) return Results.NotFound(); if (user.Username == principal.Identity!.Name && !request.IsActive) return Results.BadRequest(new { message = "نمی‌توانید حساب خودتان را غیرفعال کنید." }); user.IsActive = request.IsActive; await db.SaveChangesAsync(); return Results.NoContent(); }).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapPost("/api/users/{id:long}/reset-password", async (long id, RequestResetPassword request, SmsDbContext db, IPasswordHasher<AppUser> hasher) => { if (request.Password.Length < 8) return Results.BadRequest(new { message = "رمز عبور باید حداقل ۸ نویسه باشد." }); var user = await db.AppUsers.FindAsync(id); if (user is null) return Results.NotFound(); user.PasswordHash = hasher.HashPassword(user, request.Password); await db.SaveChangesAsync(); return Results.NoContent(); }).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapPost("/api/people/bulk", async (List<PersonInput> input, SmsDbContext db, MobileNormalizer mobiles) =>
{
    foreach (var item in input)
    {
        var mobile = mobiles.Normalize(item.Mobile);
        var person = await db.People.SingleOrDefaultAsync(x => x.NationalCode == item.NationalCode);
        if (person is null) db.People.Add(new Person { NationalCode = item.NationalCode, FirstName = item.FirstName, LastName = item.LastName, Mobile = mobile, CurrentIban = item.CurrentIban });
        else { person.FirstName = item.FirstName; person.LastName = item.LastName; person.Mobile = mobile; person.CurrentIban = item.CurrentIban; }
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { count = input.Count });
});
app.MapGet("/api/dashboard", async (SmsDbContext db) => Results.Ok(new ResponseDashboard(
    await db.People.CountAsync(),
    await db.Campaigns.CountAsync(x => x.Status == CampaignStatus.Active),
    await db.OutboundMessages.CountAsync(x => x.Status == OutboundStatus.Sent),
    await db.SecondaryBankAccounts.CountAsync(),
    await db.ReconciliationIssues.CountAsync(x => x.Status == IssueStatus.Open))));
app.MapGet("/api/templates/fields", (MessageTemplateRenderer renderer) => Results.Ok(renderer.Fields));
app.MapGet("/api/templates/confirmation-fields", (MessageTemplateRenderer renderer) => Results.Ok(renderer.ConfirmationFields));
app.MapGet("/api/people", async (string? search, int? page, int? pageSize, SmsDbContext db) =>
{
    var query = db.People.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.FirstName.Contains(term) || x.LastName.Contains(term) || x.NationalCode.Contains(term) || x.Mobile.Contains(term)); }
    var size = Math.Clamp(pageSize ?? 50, 10, 200); var current = Math.Max(page ?? 1, 1); var total = await query.CountAsync();
    var items = await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Skip((current - 1) * size).Take(size).Select(x => new { x.Id, x.FirstName, x.LastName, x.NationalCode, x.Mobile, x.CurrentIban }).ToListAsync();
    return Results.Ok(new { items, total, page = current, pageSize = size });
});
app.MapPost("/api/merchant-imports", async (IFormFile file, SmsDbContext db, IWebHostEnvironment environment, CancellationToken token) =>
{
    if (file.Length == 0) return Results.BadRequest(new { message = "فایل انتخاب‌شده خالی است." });
    if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "فقط فایل اکسل با پسوند xlsx قابل بارگذاری است." });
    var directory = Path.Combine(environment.ContentRootPath, "App_Data", "imports"); Directory.CreateDirectory(directory);
    var storedName = $"{Guid.NewGuid():N}.xlsx"; var path = Path.Combine(directory, storedName);
    await using (var target = File.Create(path)) await file.CopyToAsync(target, token);
    await using var source = File.OpenRead(path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
    if (await db.MerchantImportBatches.AnyAsync(x => x.FileHash == hash && x.Status != ImportStatus.Invalidated, token)) { File.Delete(path); return Results.Conflict(new { message = "این فایل قبلاً بارگذاری شده است." }); }
    var batch = new MerchantImportBatch { OriginalFileName = Path.GetFileName(file.FileName), StoredFileName = storedName, FileHash = hash, Status = ImportStatus.Queued, CurrentStage = "در صف پردازش" };
    db.Add(batch); await db.SaveChangesAsync(token); return Results.Accepted($"/api/merchant-imports/{batch.Id}", new { batch.Id });
}).DisableAntiforgery();
app.MapGet("/api/merchant-imports", async (SmsDbContext db) => Results.Ok(await db.MerchantImportBatches.AsNoTracking().Where(x => x.StoredFileName != "manual").OrderByDescending(x => x.Id).Take(200).ToListAsync()));
app.MapGet("/api/merchant-imports/{id:long}", async (long id, SmsDbContext db) => await db.MerchantImportBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) is { } batch ? Results.Ok(batch) : Results.NotFound());
app.MapDelete("/api/merchant-imports/{id:long}", async (long id, SmsDbContext db, IWebHostEnvironment environment, CancellationToken token) =>
{
    var batch = await db.MerchantImportBatches.SingleOrDefaultAsync(x => x.Id == id, token); if (batch is null) return Results.NotFound();
    if (batch.StoredFileName == "manual") return Results.BadRequest(new { message = "سوابق ثبت دستی از بخش فایل‌ها قابل ابطال نیست." });
    if (batch.Status is ImportStatus.Validating or ImportStatus.Importing) return Results.Conflict(new { message = "فایل در حال پردازش است و اکنون قابل ابطال نیست." });
    await db.Merchants.Where(x => x.ImportBatchId == id).ExecuteDeleteAsync(token); batch.Status = ImportStatus.Invalidated; batch.CurrentStage = "ابطال‌شده"; batch.InvalidatedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token);
    await db.People.Where(x => x.SourceImportBatchId == id && !db.OutboundMessages.Any(o => o.PersonId == x.Id) && !db.SecondaryBankAccounts.Any(a => a.PersonId == x.Id)).ExecuteDeleteAsync(token);
    var path = Path.Combine(environment.ContentRootPath, "App_Data", "imports", batch.StoredFileName); if (File.Exists(path)) File.Delete(path);
    return Results.NoContent();
});
app.MapGet("/api/merchants", async (string? search, int? page, int? pageSize, SmsDbContext db) =>
{
    var query = db.Merchants.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.TerminalNumber.Contains(term) || x.NationalId.Contains(term) || x.MerchantName.Contains(term) || (x.Bank != null && x.Bank.Contains(term)) || (x.Agency != null && x.Agency.Contains(term))); }
    var size = Math.Clamp(pageSize ?? 50, 10, 200); var current = Math.Max(page ?? 1, 1); var total = await query.CountAsync();
    var items = await query.OrderBy(x => x.MerchantName).ThenBy(x => x.TerminalNumber).Skip((current - 1) * size).Take(size)
        .Select(x => new { x.Id, x.TerminalNumber, x.NationalId, x.MerchantName, x.Mobile, x.Status, x.CurrentIban, x.Bank, x.Agency, x.PersonId, HasMobile = x.PersonId != null && db.People.Any(p => p.Id == x.PersonId && p.Mobile != "") }).ToListAsync();
    return Results.Ok(new { items, total, page = current, pageSize = size });
});
app.MapPost("/api/merchants", async (RequestCreateMerchant request, SmsDbContext db, MobileNormalizer mobiles, CancellationToken token) =>
{
    var terminal = request.TerminalNumber.Trim(); var nationalId = request.NationalId.Trim(); var name = request.MerchantName.Trim();
    if (terminal.Length == 0 || nationalId.Length == 0 || name.Length == 0) return Results.BadRequest(new { message = "شماره پایانه، شناسه ملی و نام پذیرنده الزامی است." });
    if (await db.Merchants.AnyAsync(x => x.TerminalNumber == terminal, token)) return Results.Conflict(new { message = "این شماره پایانه قبلاً ثبت شده است." });
    var manualBatch = await db.MerchantImportBatches.SingleOrDefaultAsync(x => x.StoredFileName == "manual", token);
    if (manualBatch is null) { manualBatch = new MerchantImportBatch { OriginalFileName = "ثبت دستی", StoredFileName = "manual", FileHash = "MANUAL", Status = ImportStatus.Completed, CurrentStage = "ثبت دستی", CompletedAtUtc = DateTimeOffset.UtcNow }; db.Add(manualBatch); await db.SaveChangesAsync(token); }
    string? mobile = null; if (!string.IsNullOrWhiteSpace(request.Mobile)) { try { mobile = mobiles.Normalize(request.Mobile); } catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); } }
    var person = await db.People.SingleOrDefaultAsync(x => x.NationalCode == nationalId, token);
    if (person is null && mobile is not null) { person = new Person { NationalCode = nationalId, FirstName = name, LastName = string.Empty, Mobile = mobile, CurrentIban = request.CurrentIban?.Trim() }; db.Add(person); }
    else if (person is not null && mobile is not null) person.Mobile = mobile;
    var merchant = new Merchant { ImportBatchId = manualBatch.Id, TerminalNumber = terminal, NationalId = nationalId, MerchantName = name, Mobile = mobile, Status = request.Status?.Trim(), CurrentIban = request.CurrentIban?.Trim(), Bank = request.Bank?.Trim(), Agency = request.Agency?.Trim(), PersonId = person?.Id, Person = person };
    db.Add(merchant); manualBatch.TotalRows++; manualBatch.ProcessedRows++; manualBatch.InsertedRows++; await db.SaveChangesAsync(token); return Results.Created($"/api/merchants/{merchant.Id}", new { merchant.Id });
});
app.MapPost("/api/campaigns", async (RequestCreateCampaign request, SmsDbContext db, MessageTemplateRenderer renderer) =>
{
    if (request.EndsAtUtc.HasValue && request.EndsAtUtc <= DateTimeOffset.UtcNow) return Results.BadRequest(new { message = "مهلت دریافت پاسخ باید بعد از زمان فعلی باشد." });
    var unknownFields = renderer.GetUnknownFields(request.MessageTemplate);
    if (unknownFields.Count > 0) return Results.BadRequest(new { message = "قالب شامل فیلد ناشناخته است.", unknownFields });
    if (request.ConfirmationEnabled && string.IsNullOrWhiteSpace(request.ConfirmationTemplate)) return Results.BadRequest(new { message = "برای پیام تأیید، متن پیام را وارد کنید." });
    var unknownConfirmationFields = renderer.GetUnknownConfirmationFields(request.ConfirmationTemplate ?? string.Empty);
    if (unknownConfirmationFields.Count > 0) return Results.BadRequest(new { message = "قالب پیام تأیید شامل فیلد ناشناخته است.", unknownFields = unknownConfirmationFields });
    var peopleQuery = db.People.AsNoTracking().AsQueryable();
    if (!request.SendToAll) { var ids = request.RecipientIds?.Distinct().ToArray() ?? []; if (ids.Length == 0) return Results.BadRequest(new { message = "حداقل یک مخاطب را انتخاب کنید." }); peopleQuery = peopleQuery.Where(x => ids.Contains(x.Id)); }
    var people = await peopleQuery.ToListAsync();
    if (people.Count == 0) return Results.BadRequest(new { message = "هیچ مخاطب دارای شماره موبایل برای ارسال انتخاب نشده است." });
    if (!request.SendToAll && people.Count != (request.RecipientIds?.Distinct().Count() ?? 0)) return Results.BadRequest(new { message = "یک یا چند مخاطب انتخابی معتبر نیستند." });
    var campaign = new Campaign { Name = request.Name, MessageTemplate = request.MessageTemplate, ConfirmationEnabled = request.ConfirmationEnabled, ConfirmationTemplate = request.ConfirmationEnabled ? request.ConfirmationTemplate?.Trim() : null, StartsAtUtc = DateTimeOffset.UtcNow, EndsAtUtc = request.EndsAtUtc, Status = CampaignStatus.Active };
    db.Campaigns.Add(campaign);
    await db.SaveChangesAsync();
    db.OutboundMessages.AddRange(people.Select(person => new OutboundMessage { CampaignId = campaign.Id, PersonId = person.Id, RenderedText = renderer.Render(request.MessageTemplate, person), Status = OutboundStatus.Pending }));
    await db.SaveChangesAsync();
    return Results.Created($"/api/campaigns/{campaign.Id}", new { campaign.Id, recipientCount = people.Count });
});
app.MapGet("/api/reconciliation-issues", async (SmsDbContext db) => Results.Ok(await db.ReconciliationIssues.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(500).ToListAsync()));
app.MapGet("/api/campaigns/jobs", async (SmsDbContext db) => Results.Ok(await db.Campaigns.AsNoTracking().OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Name, x.Status, x.StartsAtUtc, x.EndsAtUtc, Total = db.OutboundMessages.Count(o => o.CampaignId == x.Id), Pending = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Pending), Processing = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Processing), Sent = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Sent), Failed = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Failed) }).Take(200).ToListAsync()));
app.MapPost("/api/campaigns/{id:long}/retry-failed", async (long id, SmsDbContext db, CancellationToken token) => { var count = await db.OutboundMessages.Where(x => x.CampaignId == id && x.Status == OutboundStatus.Failed).ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, OutboundStatus.Pending).SetProperty(m => m.LastError, (string?)null), token); return Results.Ok(new { count }); });
app.MapGet("/api/inbound-messages", async (string? search, ProcessingStatus? status, int? page, int? pageSize, SmsDbContext db) =>
{
    var query = db.InboundMessages.AsNoTracking().AsQueryable();
    if (status.HasValue) query = query.Where(x => x.ProcessingStatus == status.Value);
    if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.SenderMobile.Contains(term) || x.RawText.Contains(term) || x.ProviderMessageId.Contains(term)); }
    var size = Math.Clamp(pageSize ?? 50, 10, 200); var current = Math.Max(page ?? 1, 1); var total = await query.CountAsync();
    var items = await query.OrderByDescending(x => x.ReceivedAtUtc).Skip((current - 1) * size).Take(size).Select(x => new { x.Id, x.ProviderMessageId, x.SenderMobile, x.RawText, x.ReceivedAtUtc, x.ProcessingStatus, x.LastError, Iban = db.SecondaryBankAccounts.Where(a => a.InboundMessageId == x.Id).Select(a => a.Iban).FirstOrDefault() }).ToListAsync();
    return Results.Ok(new { items, total, page = current, pageSize = size });
});
app.MapGet("/api/secondary-accounts/export.csv", async (SmsDbContext db) =>
{
    var rows = await db.SecondaryBankAccounts.AsNoTracking().Join(db.People, a => a.PersonId, p => p.Id, (a, p) => new { p.NationalCode, p.FirstName, p.LastName, p.Mobile, a.Iban, a.ReceivedAtUtc }).OrderBy(x => x.NationalCode).ToListAsync();
    var csv = new System.Text.StringBuilder("NationalCode,FirstName,LastName,Mobile,Iban,ReceivedAtUtc\r\n"); foreach (var x in rows) csv.AppendLine(string.Join(',', new[] { x.NationalCode, x.FirstName, x.LastName, x.Mobile, x.Iban, x.ReceivedAtUtc.ToString("O") }.Select(Csv)));
    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "secondary-accounts.csv");
});
app.MapGet("/api/secondary-accounts/export.xlsx", async (SmsDbContext db) =>
{
    var rows = await db.SecondaryBankAccounts.AsNoTracking().Join(db.People, a => a.PersonId, p => p.Id, (a, p) => new { p.NationalCode, p.FirstName, p.LastName, p.Mobile, a.Iban, a.ReceivedAtUtc }).OrderBy(x => x.NationalCode).ToListAsync();
    using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("شماره شباها"); var headers = new[] { "کد ملی", "نام", "نام خانوادگی", "شماره موبایل", "شماره شبا", "زمان دریافت" }; for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
    for (var i = 0; i < rows.Count; i++) { var x = rows[i]; sheet.Cell(i + 2, 1).Value = x.NationalCode; sheet.Cell(i + 2, 2).Value = x.FirstName; sheet.Cell(i + 2, 3).Value = x.LastName; sheet.Cell(i + 2, 4).Value = x.Mobile; sheet.Cell(i + 2, 5).Value = x.Iban; sheet.Cell(i + 2, 6).Value = x.ReceivedAtUtc.LocalDateTime; }
    sheet.RightToLeft = true; sheet.Row(1).Style.Font.Bold = true; sheet.Columns().AdjustToContents(); using var stream = new MemoryStream(); workbook.SaveAs(stream); return Results.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "secondary-accounts.xlsx");
});
app.Run();
static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
static string? ValidateUser(RequestCreateUser request) { if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName)) return "نام کاربری و نام نمایشی الزامی است."; if (request.Password.Length < 8) return "رمز عبور باید حداقل ۸ نویسه باشد."; return null; }
static async Task SignIn(HttpContext http, AppUser user) { var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username), new("display_name", user.DisplayName), new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User") }; await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme))); }
public sealed record PersonInput(string NationalCode, string FirstName, string LastName, string Mobile, string? CurrentIban);
public sealed record RequestCreateCampaign(string Name, string MessageTemplate, DateTimeOffset? EndsAtUtc = null, bool ConfirmationEnabled = false, string? ConfirmationTemplate = null, bool SendToAll = true, IReadOnlyList<long>? RecipientIds = null);
public sealed record ResponseDashboard(int PeopleCount, int ActiveCampaignCount, int SentCount, int ReceivedIbanCount, int OpenIssueCount);
public sealed record RequestLogin(string Username, string Password);
public sealed record RequestCreateUser(string Username, string DisplayName, string Password, bool IsAdmin = false);
public sealed record RequestUserStatus(bool IsActive);
public sealed record RequestResetPassword(string Password);
public sealed record RequestCreateMerchant(string TerminalNumber, string NationalId, string MerchantName, string? Mobile, string? Status, string? CurrentIban, string? Bank, string? Agency);
public partial class Program;
