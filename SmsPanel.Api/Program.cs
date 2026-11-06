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
builder.Services.AddSingleton<BankCardService>();
builder.Services.AddSingleton<SamanCardToIbanService>();
builder.Services.AddSingleton<DayCardToIbanService>();
builder.Services.AddSingleton<TejaratCardToIbanService>();
builder.Services.AddSingleton<BsiCardToIbanService>();
builder.Services.AddSingleton<MellatCardToIbanService>();
builder.Services.AddSingleton<IbanOwnershipVerificationService>();
builder.Services.AddSingleton<MobileNormalizer>();
builder.Services.AddSingleton<MessageTemplateRenderer>();
builder.Services.AddSingleton<InboundTextDecoder>();
builder.Services.AddSingleton<PersianPersonNameParser>();
builder.Services.AddScoped<InboundMessageProcessor>();
builder.Services.AddScoped<InboundPersonNameResolver>();
builder.Services.AddHostedService<ExistingPersonNameSplitWorker>();
builder.Services.AddHostedService<InboxPollingWorker>();
builder.Services.AddHostedService<InboundReprocessingWorker>();
builder.Services.AddHostedService<OutboundDispatchWorker>();
builder.Services.AddHostedService<MerchantImportWorker>();
builder.Services.AddHostedService<ExistingIbanOwnershipVerificationWorker>();
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
app.MapGet("/api/dashboard", async (HttpContext context, SmsDbContext db) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    return Results.Ok(new ResponseDashboard(
        await db.People.CountAsync(),
        await db.Campaigns.CountAsync(x => x.Status == CampaignStatus.Active),
        await db.OutboundMessages.CountAsync(x => x.Status == OutboundStatus.Sent),
        await db.SecondaryBankAccounts.CountAsync(),
        await db.ReconciliationIssues.CountAsync(x => x.Status == IssueStatus.Open)));
});
app.MapGet("/api/templates/fields", (MessageTemplateRenderer renderer) => Results.Ok(renderer.Fields));
app.MapGet("/api/templates/confirmation-fields", (MessageTemplateRenderer renderer) => Results.Ok(renderer.ConfirmationFields));
app.MapGet("/api/templates/invalid-reply-fields", (MessageTemplateRenderer renderer) => Results.Ok(renderer.InvalidReplyFields));
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
app.MapPost("/api/merchants", async (RequestCreateMerchant request, SmsDbContext db, MobileNormalizer mobiles, PersianPersonNameParser names, CancellationToken token) =>
{
    var terminal = request.TerminalNumber.Trim(); var nationalId = request.NationalId.Trim(); var name = request.MerchantName.Trim();
    if (terminal.Length == 0 || nationalId.Length == 0 || name.Length == 0) return Results.BadRequest(new { message = "شماره پایانه، شناسه ملی و نام پذیرنده الزامی است." });
    if (await db.Merchants.AnyAsync(x => x.TerminalNumber == terminal, token)) return Results.Conflict(new { message = "این شماره پایانه قبلاً ثبت شده است." });
    var manualBatch = await db.MerchantImportBatches.SingleOrDefaultAsync(x => x.StoredFileName == "manual", token);
    if (manualBatch is null) { manualBatch = new MerchantImportBatch { OriginalFileName = "ثبت دستی", StoredFileName = "manual", FileHash = "MANUAL", Status = ImportStatus.Completed, CurrentStage = "ثبت دستی", CompletedAtUtc = DateTimeOffset.UtcNow }; db.Add(manualBatch); await db.SaveChangesAsync(token); }
    string? mobile = null; if (!string.IsNullOrWhiteSpace(request.Mobile)) { try { mobile = mobiles.Normalize(request.Mobile); } catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); } }
    var parsedName = names.Parse(name);
    var person = await db.People.SingleOrDefaultAsync(x => x.NationalCode == nationalId, token);
    if (person is null && mobile is not null) { person = new Person { NationalCode = nationalId, FirstName = parsedName.FirstName, LastName = parsedName.LastName, Mobile = mobile, CurrentIban = request.CurrentIban?.Trim() }; db.Add(person); }
    else if (person is not null) { if (mobile is not null) person.Mobile = mobile; if (string.IsNullOrWhiteSpace(person.LastName)) { person.FirstName = parsedName.FirstName; person.LastName = parsedName.LastName; } }
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
    if (request.InvalidReplyEnabled && string.IsNullOrWhiteSpace(request.InvalidReplyTemplate)) return Results.BadRequest(new { message = "برای پاسخ خودکار نامعتبر، متن پیام را وارد کنید." });
    var unknownInvalidReplyFields = renderer.GetUnknownInvalidReplyFields(request.InvalidReplyTemplate ?? string.Empty);
    if (unknownInvalidReplyFields.Count > 0) return Results.BadRequest(new { message = "قالب پاسخ نامعتبر شامل فیلد ناشناخته است.", unknownFields = unknownInvalidReplyFields });
    var peopleQuery = db.People.AsNoTracking().AsQueryable();
    var recipientStatuses = request.RecipientStatuses?.Distinct().ToArray() ?? [];
    if (!request.SendToAll && recipientStatuses.Length > 0) peopleQuery = FilterPeopleByRecipientStatuses(peopleQuery, db, recipientStatuses);
    else if (!request.SendToAll) { var ids = request.RecipientIds?.Distinct().ToArray() ?? []; if (ids.Length == 0) return Results.BadRequest(new { message = "حداقل یک مخاطب یا وضعیت را انتخاب کنید." }); peopleQuery = peopleQuery.Where(x => ids.Contains(x.Id)); }
    var people = await peopleQuery.ToListAsync();
    if (people.Count == 0) return Results.BadRequest(new { message = "هیچ مخاطب دارای شماره موبایل برای ارسال انتخاب نشده است." });
    if (!request.SendToAll && recipientStatuses.Length == 0 && people.Count != (request.RecipientIds?.Distinct().Count() ?? 0)) return Results.BadRequest(new { message = "یک یا چند مخاطب انتخابی معتبر نیستند." });
    var campaign = new Campaign { Name = request.Name.Trim(), MessageTemplate = request.MessageTemplate, ConfirmationEnabled = request.ConfirmationEnabled, ConfirmationTemplate = request.ConfirmationEnabled ? request.ConfirmationTemplate?.Trim() : null, InvalidReplyEnabled = request.InvalidReplyEnabled, InvalidReplyTemplate = request.InvalidReplyEnabled ? request.InvalidReplyTemplate?.Trim() : null, StartsAtUtc = DateTimeOffset.UtcNow, EndsAtUtc = request.EndsAtUtc, Status = CampaignStatus.Draft };
    db.Campaigns.Add(campaign);
    await db.SaveChangesAsync();
    var personIds = people.Select(x => x.Id).ToArray();
    var merchantBanks = await db.Merchants.AsNoTracking()
        .Where(x => x.PersonId.HasValue && personIds.Contains(x.PersonId.Value) && x.Bank != null && x.Bank != "")
        .OrderByDescending(x => x.Id)
        .Select(x => new { PersonId = x.PersonId!.Value, x.Bank })
        .ToListAsync();
    var bankByPerson = merchantBanks.GroupBy(x => x.PersonId).ToDictionary(x => x.Key, x => x.First().Bank);
    db.OutboundMessages.AddRange(people.Select(person => new OutboundMessage
    {
        CampaignId = campaign.Id,
        PersonId = person.Id,
        RenderedText = renderer.Render(request.MessageTemplate, person, bankByPerson.GetValueOrDefault(person.Id)),
        Status = OutboundStatus.Pending
    }));
    await db.SaveChangesAsync();
    return Results.Created($"/api/campaigns/{campaign.Id}", new { campaign.Id, recipientCount = people.Count });
});
app.MapGet("/api/campaigns/recipient-status-counts", async (SmsDbContext db, CancellationToken token) =>
{
    var counts = new List<object>();
    foreach (var status in Enum.GetValues<CampaignRecipientStatus>())
    {
        var count = await FilterPeopleByRecipientStatuses(db.People.AsNoTracking(), db, [status]).CountAsync(token);
        counts.Add(new { status, count });
    }
    return Results.Ok(counts);
});
app.MapGet("/api/campaigns", async (string? search, CampaignStatus? status, int? page, int? pageSize, SmsDbContext db) =>
{
    var query = db.Campaigns.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.Name.Contains(term) || x.MessageTemplate.Contains(term)); }
    if (status.HasValue) query = query.Where(x => x.Status == status.Value);
    var size = Math.Clamp(pageSize ?? 30, 10, 100); var current = Math.Max(page ?? 1, 1); var total = await query.CountAsync();
    var items = await query.OrderByDescending(x => x.Id).Skip((current - 1) * size).Take(size).Select(x => new ResponseCampaignSummary(
        x.Id, x.Name, x.MessageTemplate, x.ConfirmationEnabled, x.ConfirmationTemplate, x.InvalidReplyEnabled, x.InvalidReplyTemplate, x.Status, x.StartsAtUtc, x.EndsAtUtc,
        db.OutboundMessages.Count(o => o.CampaignId == x.Id), db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Sent),
        db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Failed))).ToListAsync();
    return Results.Ok(new ResponseCampaignSearch(items, total, current, size));
});
app.MapGet("/api/campaigns/latest-start", async (SmsDbContext db, CancellationToken token) =>
    Results.Ok(new ResponseLatestCampaignStart(await db.Campaigns.AsNoTracking().Where(x => x.Status != CampaignStatus.Draft).OrderByDescending(x => x.Id).Select(x => (DateTimeOffset?)x.StartsAtUtc).FirstOrDefaultAsync(token))));
app.MapPatch("/api/campaigns/{id:long}", async (long id, RequestUpdateCampaign request, SmsDbContext db, MessageTemplateRenderer renderer, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.MessageTemplate)) return Results.BadRequest(new { message = "عنوان و متن کمپین الزامی است." });
    if (request.EndsAtUtc.HasValue && request.EndsAtUtc <= DateTimeOffset.UtcNow) return Results.BadRequest(new { message = "مهلت دریافت پاسخ باید بعد از زمان فعلی باشد." });
    var unknownFields = renderer.GetUnknownFields(request.MessageTemplate); if (unknownFields.Count > 0) return Results.BadRequest(new { message = "قالب شامل فیلد ناشناخته است.", unknownFields });
    if (request.ConfirmationEnabled && string.IsNullOrWhiteSpace(request.ConfirmationTemplate)) return Results.BadRequest(new { message = "برای پیام تأیید، متن آن را وارد کنید." });
    var unknownConfirmationFields = renderer.GetUnknownConfirmationFields(request.ConfirmationTemplate ?? string.Empty); if (unknownConfirmationFields.Count > 0) return Results.BadRequest(new { message = "قالب پیام تأیید شامل فیلد ناشناخته است.", unknownFields = unknownConfirmationFields });
    if (request.InvalidReplyEnabled && string.IsNullOrWhiteSpace(request.InvalidReplyTemplate)) return Results.BadRequest(new { message = "برای پاسخ خودکار نامعتبر، متن آن را وارد کنید." });
    var unknownInvalidReplyFields = renderer.GetUnknownInvalidReplyFields(request.InvalidReplyTemplate ?? string.Empty); if (unknownInvalidReplyFields.Count > 0) return Results.BadRequest(new { message = "قالب پاسخ نامعتبر شامل فیلد ناشناخته است.", unknownFields = unknownInvalidReplyFields });
    var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, token); if (campaign is null) return Results.NotFound();
    if (await db.OutboundMessages.AnyAsync(x => x.CampaignId == id && (x.Status == OutboundStatus.Sent || x.Status == OutboundStatus.Processing), token)) return Results.Conflict(new { message = "کمپینی که ارسال آن شروع شده است قابل ویرایش نیست." });
    var messages = await db.OutboundMessages.Where(x => x.CampaignId == id).ToListAsync(token);
    var personIds = messages.Select(x => x.PersonId).ToArray();
    var people = await db.People.Where(x => personIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
    var merchantBanks = await db.Merchants.AsNoTracking().Where(x => x.PersonId.HasValue && personIds.Contains(x.PersonId.Value) && x.Bank != null && x.Bank != "").OrderByDescending(x => x.Id).Select(x => new { PersonId = x.PersonId!.Value, x.Bank }).ToListAsync(token);
    var bankByPerson = merchantBanks.GroupBy(x => x.PersonId).ToDictionary(x => x.Key, x => x.First().Bank);
    campaign.Name = request.Name.Trim(); campaign.MessageTemplate = request.MessageTemplate; campaign.EndsAtUtc = request.EndsAtUtc; campaign.ConfirmationEnabled = request.ConfirmationEnabled; campaign.ConfirmationTemplate = request.ConfirmationEnabled ? request.ConfirmationTemplate?.Trim() : null; campaign.InvalidReplyEnabled = request.InvalidReplyEnabled; campaign.InvalidReplyTemplate = request.InvalidReplyEnabled ? request.InvalidReplyTemplate?.Trim() : null; campaign.Status = CampaignStatus.Draft;
    foreach (var message in messages) { message.RenderedText = renderer.Render(request.MessageTemplate, people[message.PersonId], bankByPerson.GetValueOrDefault(message.PersonId)); message.Status = OutboundStatus.Pending; message.AttemptCount = 0; message.ProviderReference = null; message.LastError = null; message.SentAtUtc = null; }
    await db.SaveChangesAsync(token); return Results.Ok(new ResponseUpdateCampaign(campaign.Id, campaign.Status));
});
app.MapPost("/api/campaigns/{id:long}/start", async (long id, SmsDbContext db, CancellationToken token) =>
{
    var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token); if (campaign is null) return Results.NotFound();
    if (campaign.Status != CampaignStatus.Draft) return Results.Conflict(new { message = "فقط کمپین آماده ارسال را می‌توان شروع کرد." });
    if (campaign.EndsAtUtc.HasValue && campaign.EndsAtUtc <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "مهلت کمپین گذشته است؛ ابتدا آن را ویرایش کنید." });
    if (!await db.OutboundMessages.AnyAsync(x => x.CampaignId == id, token)) return Results.Conflict(new { message = "این کمپین گیرنده‌ای ندارد." });
    var changed = await db.Campaigns.Where(x => x.Id == id && x.Status == CampaignStatus.Draft).ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, CampaignStatus.Active).SetProperty(c => c.StartsAtUtc, DateTimeOffset.UtcNow), token);
    return changed == 1 ? Results.Ok(new ResponseStartCampaign(id, CampaignStatus.Active)) : Results.Conflict(new { message = "وضعیت کمپین تغییر کرده است؛ صفحه را تازه‌سازی کنید." });
});
app.MapGet("/api/reconciliation-issues", async (SmsDbContext db, CancellationToken token) =>
{
    var issues = await db.ReconciliationIssues.AsNoTracking().Where(x => x.Status == IssueStatus.Open).OrderByDescending(x => x.CreatedAtUtc).Take(500).ToListAsync(token);
    var messages = await db.InboundMessages.AsNoTracking().Where(x => issues.Select(i => i.InboundMessageId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
    var mobiles = issues.Select(x => x.SenderMobile).Distinct().ToList();
    var matchedPersonIds = messages.Values.Where(x => x.MatchedPersonId.HasValue).Select(x => x.MatchedPersonId!.Value).Distinct().ToList();
    var loadedPeople = await db.People.AsNoTracking().Where(x => matchedPersonIds.Contains(x.Id) || mobiles.Contains(x.Mobile)).ToListAsync(token);
    var peopleById = loadedPeople.ToDictionary(x => x.Id);
    var peopleByMobile = loadedPeople.GroupBy(x => x.Mobile).Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single());
    var campaignIds = issues.Where(x => x.CampaignId.HasValue).Select(x => x.CampaignId!.Value).Distinct().ToList();
    var campaigns = await db.Campaigns.AsNoTracking().Where(x => campaignIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, token);
    var personIds = loadedPeople.Select(x => x.Id).Distinct().ToList();
    var accounts = await db.SecondaryBankAccounts.AsNoTracking().Where(x => personIds.Contains(x.PersonId) && campaignIds.Contains(x.CampaignId)).ToListAsync(token);
    var result = issues.Select(issue =>
    {
        messages.TryGetValue(issue.InboundMessageId, out var message);
        Person? person = null;
        if (message?.MatchedPersonId is long matchedPersonId) peopleById.TryGetValue(matchedPersonId, out person);
        if (person is null) peopleByMobile.TryGetValue(issue.SenderMobile, out person);
        var campaignName = issue.CampaignId.HasValue && campaigns.TryGetValue(issue.CampaignId.Value, out var name) ? name : null;
        var currentRegisteredIban = person is null || !issue.CampaignId.HasValue ? null : accounts.FirstOrDefault(x => x.PersonId == person.Id && x.CampaignId == issue.CampaignId.Value)?.Iban;
        return new ResponseReconciliationIssue(issue.Id, issue.InboundMessageId, issue.SenderMobile, issue.ExtractedIban, issue.IssueType, issue.CreatedAtUtc, issue.CampaignId, campaignName, person?.Id, person is null ? null : $"{person.FirstName} {person.LastName}".Trim(), currentRegisteredIban, message?.RawText, message?.ReceivedAtUtc, person is not null && issue.CampaignId.HasValue);
    }).ToList();
    return Results.Ok(result);
});
app.MapPost("/api/reconciliation-issues/{id:long}/finalize", async (long id, RequestFinalizeReconciliationIssue request, ClaimsPrincipal principal, SmsDbContext db, IbanService ibans, CancellationToken token) =>
{
    var candidates = ibans.Extract(request.Iban ?? string.Empty).Concat(ibans.ExtractCorrectable(request.Iban ?? string.Empty)).Where(ibans.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (candidates.Count != 1) return Results.BadRequest(new { message = "یک شماره شبای معتبر و بدون ابهام وارد کنید." });
    var finalIban = candidates[0];
    var issue = await db.ReconciliationIssues.SingleOrDefaultAsync(x => x.Id == id && x.Status == IssueStatus.Open, token);
    if (issue is null) return Results.NotFound(new { message = "مغایرت باز یافت نشد یا قبلاً نهایی شده است." });
    if (!issue.CampaignId.HasValue) return Results.Conflict(new { message = "این مغایرت به کمپین مشخصی متصل نیست و قابل نهایی‌سازی خودکار نیست." });
    var person = await ResolveIssuePersonAsync(db, issue, token);
    if (person is null) return Results.Conflict(new { message = "مخاطب این پیام به‌صورت یکتا قابل تشخیص نیست." });
    if (person.CurrentIban?.Equals(finalIban, StringComparison.OrdinalIgnoreCase) == true) return Results.Conflict(new { message = "شبای نهایی نمی‌تواند با شبای فعلی مخاطب یکسان باشد." });
    if (await db.SecondaryBankAccounts.AnyAsync(x => x.Iban == finalIban && x.PersonId != person.Id, token)) return Results.Conflict(new { message = "این شماره شبا قبلاً برای مخاطب دیگری ثبت شده است." });
    var message = await db.InboundMessages.SingleAsync(x => x.Id == issue.InboundMessageId, token);
    var account = await db.SecondaryBankAccounts.SingleOrDefaultAsync(x => x.PersonId == person.Id && x.CampaignId == issue.CampaignId.Value, token);
    if (account is null)
    {
        account = new SecondaryBankAccount { PersonId = person.Id, CampaignId = issue.CampaignId.Value, InboundMessageId = issue.InboundMessageId, Iban = finalIban, OwnershipVerified = null, ReceivedAtUtc = message.ReceivedAtUtc };
        db.SecondaryBankAccounts.Add(account);
    }
    else
    {
        account.Iban = finalIban; account.InboundMessageId = issue.InboundMessageId; account.ReceivedAtUtc = message.ReceivedAtUtc;
    }
    var relatedIssues = await db.ReconciliationIssues.Where(x => x.Status == IssueStatus.Open && x.CampaignId == issue.CampaignId &&
        (message.MatchedPersonId.HasValue
            ? db.InboundMessages.Any(inbound => inbound.Id == x.InboundMessageId && inbound.MatchedPersonId == person.Id)
            : x.SenderMobile == issue.SenderMobile)).ToListAsync(token);
    var relatedMessageIds = relatedIssues.Select(x => x.InboundMessageId).Distinct().ToList();
    var resolution = $"ثبت دستی شبای نهایی توسط {principal.Identity?.Name ?? "کاربر سامانه"}." + (string.IsNullOrWhiteSpace(request.Note) ? string.Empty : $" {request.Note.Trim()}");
    foreach (var relatedIssue in relatedIssues) { relatedIssue.Status = IssueStatus.Resolved; relatedIssue.ResolutionNote = resolution; relatedIssue.ResolvedAtUtc = DateTimeOffset.UtcNow; }
    var relatedMessages = await db.InboundMessages.Where(x => relatedMessageIds.Contains(x.Id)).ToListAsync(token);
    foreach (var relatedMessage in relatedMessages) { relatedMessage.ProcessingStatus = ProcessingStatus.ResolvedManually; relatedMessage.LastError = null; }
    await db.SaveChangesAsync(token);
    return Results.Ok(new ResponseFinalizeReconciliationIssue(issue.Id, person.Id, issue.CampaignId.Value, finalIban, relatedIssues.Count));
});
app.MapPost("/api/reconciliation-issues/{id:long}/card-conversion/start", async (long id, ClaimsPrincipal principal, SmsDbContext db, BankCardService cards, SamanCardToIbanService saman, DayCardToIbanService day, TejaratCardToIbanService tejarat, BsiCardToIbanService bsi, MellatCardToIbanService mellat, CancellationToken token) =>
{
    var issue = await db.ReconciliationIssues.SingleOrDefaultAsync(x => x.Id == id && x.Status == IssueStatus.Open && x.IssueType == IssueType.BankCardNumberReceived, token);
    if (issue is null) return Results.NotFound(new { message = "درخواست باز تبدیل شماره کارت یافت نشد." });
    var message = await db.InboundMessages.AsNoTracking().SingleAsync(x => x.Id == issue.InboundMessageId, token);
    var cardNumbers = cards.ExtractValid(message.RawText);
    if (cardNumbers.Count != 1) return Results.Conflict(new { message = "باید دقیقاً یک شماره کارت معتبر در پاسخ وجود داشته باشد." });
    try
    {
        var owner = principal.Identity?.Name ?? string.Empty;
        if (cardNumbers[0].StartsWith("621986", StringComparison.Ordinal))
        {
            var result = await saman.StartAsync(owner, cardNumbers[0], token);
            return Results.Ok(new ResponseStartCardConversion("saman_" + result.SessionId, result.CaptchaImage, result.ExpiresAtUtc, "بانک سامان", cardNumbers[0][^4..]));
        }
        if (cardNumbers[0].StartsWith("502938", StringComparison.Ordinal))
        {
            var result = await day.StartAsync(owner, cardNumbers[0], token);
            return Results.Ok(new ResponseStartCardConversion("day_" + result.SessionId, result.CaptchaImage, result.ExpiresAtUtc, "بانک دی", cardNumbers[0][^4..]));
        }
        if (cardNumbers[0].StartsWith("585983", StringComparison.Ordinal) || cardNumbers[0].StartsWith("627353", StringComparison.Ordinal))
        {
            var result = await tejarat.StartAsync(owner, cardNumbers[0], token);
            return Results.Ok(new ResponseStartCardConversion("tejarat_" + result.SessionId, result.CaptchaImage, result.ExpiresAtUtc, "بانک تجارت", cardNumbers[0][^4..], null, result.CaptchaHint));
        }
        if (cardNumbers[0].StartsWith("603769", StringComparison.Ordinal))
        {
            var conversion = await bsi.ConvertAsync(cardNumbers[0], token);
            var review = await CheckConvertedAccountHolderAsync(db, issue, conversion, token);
            return Results.Ok(new ResponseStartCardConversion(string.Empty, string.Empty, DateTimeOffset.UtcNow, "بانک صادرات", cardNumbers[0][^4..], conversion.Iban, null, conversion.AccountHolderName, review.RequiresReview, review.Reason));
        }
        if (cardNumbers[0].StartsWith("610433", StringComparison.Ordinal) || cardNumbers[0].StartsWith("991975", StringComparison.Ordinal))
        {
            var result = await mellat.StartAsync(owner, cardNumbers[0], token);
            return Results.Ok(new ResponseStartCardConversion("mellat_" + result.SessionId, result.CaptchaImage, result.ExpiresAtUtc, "بانک ملت", cardNumbers[0][^4..]));
        }
        return Results.Conflict(new { message = "برای بانک صادرکننده این کارت هنوز سرویس رسمی تبدیل فعال نشده است." });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapPost("/api/reconciliation-issues/{id:long}/card-conversion/complete", async (long id, RequestCompleteCardConversion request, ClaimsPrincipal principal, SmsDbContext db, SamanCardToIbanService saman, DayCardToIbanService day, TejaratCardToIbanService tejarat, MellatCardToIbanService mellat, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.Captcha)) return Results.BadRequest(new { message = "کد امنیتی را وارد کنید." });
    var issue = await db.ReconciliationIssues.SingleOrDefaultAsync(x => x.Id == id && x.Status == IssueStatus.Open && x.IssueType == IssueType.BankCardNumberReceived, token);
    if (issue is null) return Results.NotFound(new { message = "درخواست باز تبدیل شماره کارت یافت نشد." });
    try
    {
        var owner = principal.Identity?.Name ?? string.Empty;
        var conversion = request.SessionId.StartsWith("saman_", StringComparison.Ordinal)
            ? await saman.CompleteAsync(owner, request.SessionId[6..], request.Captcha, token)
            : request.SessionId.StartsWith("day_", StringComparison.Ordinal)
                ? await day.CompleteAsync(owner, request.SessionId[4..], request.Captcha, token)
                : request.SessionId.StartsWith("tejarat_", StringComparison.Ordinal)
                    ? await tejarat.CompleteAsync(owner, request.SessionId[8..], request.Captcha, token)
                : request.SessionId.StartsWith("mellat_", StringComparison.Ordinal)
                    ? await mellat.CompleteAsync(owner, request.SessionId[7..], request.Captcha, token)
                : throw new InvalidOperationException("نشست تبدیل بانک معتبر نیست؛ تصویر جدید بگیرید.");
        var review = await CheckConvertedAccountHolderAsync(db, issue, conversion, token);
        return Results.Ok(new ResponseCompleteCardConversion(conversion.Iban, conversion.AccountHolderName, review.RequiresReview, review.Reason));
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapPost("/api/reconciliation-issues/{id:long}/send-message", async (long id, RequestSendReconciliationIssueMessage request, ClaimsPrincipal principal, SmsDbContext db, MobileNormalizer mobiles, IFarapayamakClient client, CancellationToken token) =>
{
    var text = request.Text?.Trim() ?? string.Empty;
    if (text.Length == 0 || text.Length > 500) return Results.BadRequest(new { message = "متن پیام باید بین ۱ تا ۵۰۰ نویسه باشد." });
    var issue = await db.ReconciliationIssues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == IssueStatus.Open, token);
    if (issue is null) return Results.NotFound(new { message = "مغایرت باز یافت نشد یا قبلاً نهایی شده است." });
    string mobile;
    try { mobile = mobiles.Normalize(issue.SenderMobile); }
    catch (ArgumentException) { return Results.Conflict(new { message = "شماره موبایل این پیام معتبر نیست." }); }
    var logEntry = new ReconciliationIssueMessage { ReconciliationIssueId = id, Text = text, Status = OutboundStatus.Processing, SentBy = principal.Identity?.Name ?? "system" };
    db.ReconciliationIssueMessages.Add(logEntry);
    await db.SaveChangesAsync(token);
    try
    {
        var provider = await client.SendAsync([mobile], text, token);
        logEntry.ProviderReference = provider.Value;
        logEntry.Status = provider.IsSuccess ? OutboundStatus.Sent : OutboundStatus.Failed;
        logEntry.SentAtUtc = provider.IsSuccess ? DateTimeOffset.UtcNow : null;
        logEntry.LastError = provider.IsSuccess ? null : provider.StrRetStatus ?? $"خطای سرویس پیامک ({provider.Value})";
        await db.SaveChangesAsync(token);
        return provider.IsSuccess
            ? Results.Ok(new ResponseSendReconciliationIssueMessage(logEntry.Id, logEntry.Status, logEntry.ProviderReference))
            : Results.Json(new { message = logEntry.LastError, logEntry.Id }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
    {
        logEntry.Status = OutboundStatus.Failed; logEntry.LastError = ex.Message; await db.SaveChangesAsync(CancellationToken.None);
        return Results.Json(new { message = "ارسال پیام اختصاصی با خطا مواجه شد.", logEntry.Id }, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapGet("/api/campaigns/jobs", async (SmsDbContext db) => Results.Ok(await db.Campaigns.AsNoTracking().OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Name, x.Status, x.StartsAtUtc, x.EndsAtUtc, Total = db.OutboundMessages.Count(o => o.CampaignId == x.Id), Pending = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Pending), Processing = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Processing), Sent = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Sent), Failed = db.OutboundMessages.Count(o => o.CampaignId == x.Id && o.Status == OutboundStatus.Failed) }).Take(200).ToListAsync()));
app.MapGet("/api/campaigns/{id:long}/failed-messages", async (long id, int? page, int? pageSize, SmsDbContext db, CancellationToken token) =>
{
    if (!await db.Campaigns.AnyAsync(x => x.Id == id, token)) return Results.NotFound();
    var query = db.OutboundMessages.AsNoTracking().Where(x => x.CampaignId == id && x.Status == OutboundStatus.Failed);
    var current = Math.Max(page ?? 1, 1); var size = Math.Clamp(pageSize ?? 50, 10, 200); var total = await query.CountAsync(token);
    var items = await query.OrderBy(x => x.Id).Skip((current - 1) * size).Take(size)
        .Join(db.People.AsNoTracking(), message => message.PersonId, person => person.Id,
            (message, person) => new ResponseFailedOutboundMessage(message.Id, $"{person.FirstName} {person.LastName}".Trim(), person.Mobile, message.AttemptCount, message.LastError ?? "علت خطا ثبت نشده است."))
        .ToListAsync(token);
    return Results.Ok(new ResponseFailedOutboundMessageSearch(items, total, current, size));
});
app.MapPost("/api/campaigns/{id:long}/retry-failed", async (long id, SmsDbContext db, CancellationToken token) =>
{
    var campaign = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id, token);
    if (campaign is null) return Results.NotFound();
    var count = await db.OutboundMessages.Where(x => x.CampaignId == id && x.Status == OutboundStatus.Failed)
        .ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, OutboundStatus.Pending).SetProperty(m => m.LastError, (string?)null), token);
    if (count > 0) { campaign.Status = CampaignStatus.Active; await db.SaveChangesAsync(token); }
    return Results.Ok(new { count });
});
app.MapGet("/api/inbound-messages", async (string? search, ProcessingStatus? status, IssueType? issueType, long? campaignId, DateTimeOffset? fromUtc, int? page, int? pageSize, SmsDbContext db, IbanService ibans) =>
{
    var query = ApplyInboundMessageFilters(db.InboundMessages.AsNoTracking(), db, search, status, issueType, campaignId, fromUtc);
    var size = Math.Clamp(pageSize ?? 50, 10, 200); var current = Math.Max(page ?? 1, 1); var total = await query.CountAsync();
    var rows = await query.OrderByDescending(x => x.ReceivedAtUtc).Skip((current - 1) * size).Take(size).Select(x => new { x.Id, x.ProviderMessageId, x.SenderMobile, x.RawText, x.ReceivedAtUtc, x.ProcessingStatus, x.LastError, x.MatchedPersonId, RegisteredIban = db.SecondaryBankAccounts.Where(a => a.InboundMessageId == x.Id).Select(a => a.Iban).FirstOrDefault(), RegisteredPersonId = db.SecondaryBankAccounts.Where(a => a.InboundMessageId == x.Id).Select(a => (long?)a.PersonId).FirstOrDefault(), OwnershipVerified = db.SecondaryBankAccounts.Where(a => a.InboundMessageId == x.Id).Select(a => a.OwnershipVerified).FirstOrDefault(), SuccessfulCampaignId = db.SecondaryBankAccounts.Where(a => a.InboundMessageId == x.Id).Select(a => (long?)a.CampaignId).FirstOrDefault(), IssueId = db.ReconciliationIssues.Where(r => r.InboundMessageId == x.Id && r.Status == IssueStatus.Open).OrderByDescending(r => r.Id).Select(r => (long?)r.Id).FirstOrDefault(), IssueCampaignId = db.ReconciliationIssues.Where(r => r.InboundMessageId == x.Id && r.CampaignId != null).OrderByDescending(r => r.Id).Select(r => r.CampaignId).FirstOrDefault(), IssueIban = db.ReconciliationIssues.Where(r => r.InboundMessageId == x.Id && r.ExtractedIban != null).OrderByDescending(r => r.Id).Select(r => r.ExtractedIban).FirstOrDefault(), IssueType = db.ReconciliationIssues.Where(r => r.InboundMessageId == x.Id && r.Status == IssueStatus.Open).OrderByDescending(r => r.Id).Select(r => (IssueType?)r.IssueType).FirstOrDefault() }).ToListAsync();
    var senderMobiles = rows.Select(x => x.SenderMobile).Distinct().ToList();
    var people = (await db.People.AsNoTracking().Where(x => senderMobiles.Contains(x.Mobile)).ToListAsync()).GroupBy(x => x.Mobile).Where(x => x.Count() == 1).ToDictionary(x => x.Key, x => x.Single());
    var registeredPersonIds = rows.Select(x => x.RegisteredPersonId ?? x.MatchedPersonId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
    var registeredPeople = await db.People.AsNoTracking().Where(x => registeredPersonIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
    var personIds = people.Values.Select(x => x.Id).Concat(registeredPersonIds).Distinct().ToList();
    var banks = (await db.Merchants.AsNoTracking().Where(x => x.PersonId.HasValue && personIds.Contains(x.PersonId.Value) && x.Bank != null && x.Bank != "").OrderByDescending(x => x.Id).Select(x => new { PersonId = x.PersonId!.Value, x.Bank }).ToListAsync()).GroupBy(x => x.PersonId).ToDictionary(x => x.Key, x => x.First().Bank);
    var campaignIds = rows.Select(x => x.SuccessfulCampaignId ?? x.IssueCampaignId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
    var campaignNames = await db.Campaigns.AsNoTracking().Where(x => campaignIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name);
    var previousCandidates = await db.InboundMessages.AsNoTracking()
        .Where(message => senderMobiles.Contains(message.SenderMobile) && (db.SecondaryBankAccounts.Any(account => account.InboundMessageId == message.Id && campaignIds.Contains(account.CampaignId)) || db.ReconciliationIssues.Any(issue => issue.InboundMessageId == message.Id && issue.CampaignId.HasValue && campaignIds.Contains(issue.CampaignId.Value))))
        .Select(message => new { message.Id, message.SenderMobile, message.RawText, message.ReceivedAtUtc, SuccessfulCampaignId = db.SecondaryBankAccounts.Where(account => account.InboundMessageId == message.Id).Select(account => (long?)account.CampaignId).FirstOrDefault(), IssueCampaignId = db.ReconciliationIssues.Where(issue => issue.InboundMessageId == message.Id && issue.CampaignId != null).OrderByDescending(issue => issue.Id).Select(issue => issue.CampaignId).FirstOrDefault() })
        .ToListAsync();
    var items = rows.Select(x =>
    {
        var extracted = ibans.Extract(x.RawText).Concat(ibans.ExtractCorrectable(x.RawText)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var iban = x.RegisteredIban ?? x.IssueIban ?? (extracted.Count == 1 ? extracted[0] : null);
        people.TryGetValue(x.SenderMobile, out var person);
        var linkedPersonId = x.RegisteredPersonId ?? x.MatchedPersonId;
        if (person is null && linkedPersonId.HasValue) registeredPeople.TryGetValue(linkedPersonId.Value, out person);
        var bank = person is not null && banks.TryGetValue(person.Id, out var currentBank) ? currentBank : null;
        var relatedCampaignId = x.SuccessfulCampaignId ?? x.IssueCampaignId;
        var campaignName = relatedCampaignId.HasValue && campaignNames.TryGetValue(relatedCampaignId.Value, out var relatedCampaignName) ? relatedCampaignName : null;
        var previousReplies = x.IssueType == IssueType.MultipleDifferentReplies && relatedCampaignId.HasValue
            ? previousCandidates.Where(previous => previous.Id != x.Id && previous.SenderMobile == x.SenderMobile && (previous.SuccessfulCampaignId ?? previous.IssueCampaignId) == relatedCampaignId && (previous.ReceivedAtUtc < x.ReceivedAtUtc || (previous.ReceivedAtUtc == x.ReceivedAtUtc && previous.Id < x.Id))).OrderByDescending(previous => previous.ReceivedAtUtc).ThenByDescending(previous => previous.Id).Take(10).Select(previous =>
            {
                var previousIbans = ibans.Extract(previous.RawText).Concat(ibans.ExtractCorrectable(previous.RawText)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new ResponsePreviousInboundReply(previous.Id, previous.RawText, previous.ReceivedAtUtc, previousIbans.Count == 1 ? previousIbans[0] : null);
            }).ToList()
            : [];
        return new ResponseInboundMessage(x.Id, x.ProviderMessageId, x.SenderMobile, x.RawText, x.ReceivedAtUtc, x.ProcessingStatus, x.LastError, iban, x.IssueType, person is null ? null : $"{person.FirstName} {person.LastName}".Trim(), person?.CurrentIban, bank, relatedCampaignId, campaignName, x.IssueId, x.RegisteredIban, x.OwnershipVerified, x.IssueId.HasValue && person is not null && relatedCampaignId.HasValue, previousReplies);
    }).ToList();
    return Results.Ok(new { items, total, page = current, pageSize = size });
});
app.MapGet("/api/inbound-messages/bulk-recipient-count", async (string? search, ProcessingStatus? status, IssueType? issueType, long? campaignId, DateTimeOffset? fromUtc, SmsDbContext db, CancellationToken token) =>
{
    var query = ApplyInboundMessageFilters(db.InboundMessages.AsNoTracking(), db, search, status, issueType, campaignId, fromUtc);
    var messageCount = await query.CountAsync(token);
    var (people, uniqueMobileCount) = await FindEligibleInboundRecipients(query, db, token);
    return Results.Ok(new ResponseBulkInboundRecipientCount(messageCount, uniqueMobileCount, people.Count, uniqueMobileCount - people.Count));
});
app.MapPost("/api/inbound-messages/bulk-message", async (RequestBulkInboundMessage request, SmsDbContext db, CancellationToken token) =>
{
    var text = request.Text?.Trim() ?? string.Empty;
    if (text.Length == 0 || text.Length > 500) return Results.BadRequest(new { message = "متن پیام باید بین ۱ تا ۵۰۰ نویسه باشد." });
    var query = ApplyInboundMessageFilters(db.InboundMessages.AsNoTracking(), db, request.Search, request.Status, request.IssueType, request.CampaignId, request.FromUtc);
    var (people, uniqueMobileCount) = await FindEligibleInboundRecipients(query, db, token);
    if (people.Count == 0) return Results.BadRequest(new { message = "در نتایج فعلی مخاطب یکتای قابل‌ارسالی یافت نشد." });
    var campaignName = string.IsNullOrWhiteSpace(request.Name) ? $"ارسال گروهی پیام‌های دریافتی - {DateTimeOffset.Now:yyyy-MM-dd HH:mm}" : request.Name.Trim();
    if (campaignName.Length > 200) return Results.BadRequest(new { message = "عنوان ارسال نباید بیش از ۲۰۰ نویسه باشد." });
    long campaignId = 0;
    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var campaign = new Campaign { Name = campaignName, MessageTemplate = text, ConfirmationEnabled = false, InvalidReplyEnabled = false, StartsAtUtc = DateTimeOffset.UtcNow, EndsAtUtc = null, Status = CampaignStatus.Active };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(token);
        db.OutboundMessages.AddRange(people.Select(person => new OutboundMessage { CampaignId = campaign.Id, PersonId = person.Id, RenderedText = text, Status = OutboundStatus.Pending }));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        campaignId = campaign.Id;
    });
    return Results.Created($"/api/campaigns/{campaignId}", new ResponseBulkInboundMessage(campaignId, people.Count, uniqueMobileCount - people.Count));
});
app.MapPost("/api/inbound-messages/{id:long}/retry", async (long id, SmsDbContext db, InboundMessageProcessor processor, CancellationToken token) =>
{
    var message = await db.InboundMessages.SingleOrDefaultAsync(x => x.Id == id, token); if (message is null) return Results.NotFound();
    await db.ReconciliationIssues.Where(x => x.InboundMessageId == id).ExecuteDeleteAsync(token); message.ProcessingStatus = ProcessingStatus.Pending; message.LastError = null; await db.SaveChangesAsync(token);
    await processor.ProcessAsync(message, token); return Results.Ok(new { message.Id, message.ProcessingStatus });
});
app.MapPost("/api/inbound-messages/reprocess-no-iban", async (SmsDbContext db, InboundMessageProcessor processor, CancellationToken token) =>
{
    var messageIds = await db.ReconciliationIssues.AsNoTracking()
        .Where(x => x.Status == IssueStatus.Open && (x.IssueType == IssueType.MobileNotFound || x.IssueType == IssueType.NoIbanFound || x.IssueType == IssueType.MultipleIbansFound))
        .Select(x => x.InboundMessageId).Distinct().ToListAsync(token);
    var processed = 0; var stillNeedsReview = 0;
    foreach (var id in messageIds)
    {
        var message = await db.InboundMessages.SingleOrDefaultAsync(x => x.Id == id, token);
        if (message is null) continue;
        await db.ReconciliationIssues.Where(x => x.InboundMessageId == id && x.Status == IssueStatus.Open).ExecuteDeleteAsync(token);
        message.ProcessingStatus = ProcessingStatus.Pending; message.LastError = null; await db.SaveChangesAsync(token);
        await processor.ProcessAsync(message, token, sendConfirmation: false);
        if (message.ProcessingStatus == ProcessingStatus.Processed) processed++; else stillNeedsReview++;
    }
    return Results.Ok(new ResponseReprocessInboundMessages(messageIds.Count, processed, stillNeedsReview));
});
app.MapGet("/api/inbound-messages/export.xlsx", async (string? search, ProcessingStatus? status, IssueType? issueType, long? campaignId, DateTimeOffset? fromUtc, SmsDbContext db, IbanService ibans, CancellationToken token) =>
{
    var query = ApplyInboundMessageFilters(db.InboundMessages.AsNoTracking(), db, search, status, issueType, campaignId, fromUtc);
    var rows = await query.OrderByDescending(x => x.ReceivedAtUtc).Select(x => new { x.Id, x.ProviderMessageId, x.SenderMobile, x.RawText, x.ReceivedAtUtc, x.ProcessingStatus, x.LastError, NationalCode = db.People.Where(p => p.Id == x.MatchedPersonId || p.Mobile == x.SenderMobile).OrderByDescending(p => p.Id == x.MatchedPersonId).Select(p => p.NationalCode).FirstOrDefault(), TerminalNumber = db.Merchants.Where(m => db.People.Any(p => p.Id == m.PersonId && (p.Id == x.MatchedPersonId || p.Mobile == x.SenderMobile))).OrderByDescending(m => m.PersonId == x.MatchedPersonId).ThenByDescending(m => m.Id).Select(m => m.TerminalNumber).FirstOrDefault(), RegisteredIban = db.SecondaryBankAccounts.Where(a => a.InboundMessageId == x.Id).Select(a => a.Iban).FirstOrDefault(), IssueIban = db.ReconciliationIssues.Where(r => r.InboundMessageId == x.Id && r.ExtractedIban != null).OrderByDescending(r => r.Id).Select(r => r.ExtractedIban).FirstOrDefault(), IssueType = db.ReconciliationIssues.Where(r => r.InboundMessageId == x.Id && r.Status == IssueStatus.Open).OrderByDescending(r => r.Id).Select(r => (IssueType?)r.IssueType).FirstOrDefault() }).ToListAsync(token);
    using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("پیام‌های دریافتی");
    var headers = new[] { "شناسه ردیف", "زمان دریافت", "شماره فرستنده", "شماره پایانه", "کد ملی", "متن پیام", "وضعیت", "روش نهایی‌شدن", "دلیل بررسی", "شماره شبا", "شناسه پیام سرویس" };
    for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
    for (var index = 0; index < rows.Count; index++)
    {
        var row = rows[index]; var extracted = ibans.Extract(row.RawText).Concat(ibans.ExtractCorrectable(row.RawText)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); var iban = row.RegisteredIban ?? row.IssueIban ?? (extracted.Count == 1 ? extracted[0] : null);
        sheet.Cell(index + 2, 1).Value = row.Id; sheet.Cell(index + 2, 2).Value = row.ReceivedAtUtc.LocalDateTime; sheet.Cell(index + 2, 3).Value = row.SenderMobile; sheet.Cell(index + 2, 4).Value = row.TerminalNumber ?? string.Empty; sheet.Cell(index + 2, 5).Value = row.NationalCode ?? string.Empty; sheet.Cell(index + 2, 6).Value = row.RawText;
        sheet.Cell(index + 2, 7).Value = row.ProcessingStatus is ProcessingStatus.Processed or ProcessingStatus.ResolvedManually ? "پاسخ موفق" : row.ProcessingStatus == ProcessingStatus.ReconciliationRequired ? "نیازمند بررسی" : row.ProcessingStatus == ProcessingStatus.Pending ? "در انتظار پردازش" : "خطای پردازش";
        sheet.Cell(index + 2, 8).Value = row.ProcessingStatus == ProcessingStatus.ResolvedManually ? "دستی" : row.ProcessingStatus == ProcessingStatus.Processed ? "خودکار" : string.Empty;
        sheet.Cell(index + 2, 9).Value = row.IssueType.HasValue ? IssueLabel(row.IssueType.Value) : string.Empty; sheet.Cell(index + 2, 10).Value = iban ?? string.Empty; sheet.Cell(index + 2, 11).Value = row.ProviderMessageId;
    }
    sheet.RightToLeft = true; sheet.Row(1).Style.Font.Bold = true; sheet.SheetView.FreezeRows(1); sheet.Columns().AdjustToContents(8, 60); using var stream = new MemoryStream(); workbook.SaveAs(stream);
    return Results.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "inbound-messages.xlsx");
});
app.MapGet("/api/secondary-accounts/export.csv", async (long? campaignId, SmsDbContext db) =>
{
    var query = db.SecondaryBankAccounts.AsNoTracking(); if (campaignId.HasValue) query = query.Where(x => x.CampaignId == campaignId.Value);
    var rows = await query.Join(db.People, a => a.PersonId, p => p.Id, (a, p) => new { Account = a, Person = p }).Join(db.InboundMessages, x => x.Account.InboundMessageId, m => m.Id, (x, m) => new { x.Account.Id, x.Person.NationalCode, x.Person.FirstName, x.Person.LastName, x.Person.Mobile, x.Account.Iban, x.Account.ReceivedAtUtc, m.ProcessingStatus }).OrderByDescending(x => x.ReceivedAtUtc).ToListAsync();
    var csv = new System.Text.StringBuilder("RowId,NationalCode,FirstName,LastName,Mobile,Iban,ReceivedAtUtc,FinalizationMethod\r\n"); foreach (var x in rows) csv.AppendLine(string.Join(',', new[] { x.Id.ToString(), x.NationalCode, x.FirstName, x.LastName, x.Mobile, x.Iban, x.ReceivedAtUtc.ToString("O"), x.ProcessingStatus == ProcessingStatus.ResolvedManually ? "دستی" : "خودکار" }.Select(Csv)));
    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "secondary-accounts.csv");
});
app.MapGet("/api/secondary-accounts/export.xlsx", async (long? campaignId, SmsDbContext db) =>
{
    var query = db.SecondaryBankAccounts.AsNoTracking(); if (campaignId.HasValue) query = query.Where(x => x.CampaignId == campaignId.Value);
    var rows = await query.Join(db.People, a => a.PersonId, p => p.Id, (a, p) => new { Account = a, Person = p }).Join(db.InboundMessages, x => x.Account.InboundMessageId, m => m.Id, (x, m) => new { x.Account.Id, x.Person.NationalCode, x.Person.FirstName, x.Person.LastName, x.Person.Mobile, x.Account.Iban, x.Account.ReceivedAtUtc, m.ProcessingStatus }).OrderByDescending(x => x.ReceivedAtUtc).ToListAsync();
    using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("شماره شباها"); var headers = new[] { "شناسه ردیف", "کد ملی", "نام", "نام خانوادگی", "شماره موبایل", "شماره شبا", "زمان دریافت", "روش نهایی‌شدن" }; for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
    for (var i = 0; i < rows.Count; i++) { var x = rows[i]; sheet.Cell(i + 2, 1).Value = x.Id; sheet.Cell(i + 2, 2).Value = x.NationalCode; sheet.Cell(i + 2, 3).Value = x.FirstName; sheet.Cell(i + 2, 4).Value = x.LastName; sheet.Cell(i + 2, 5).Value = x.Mobile; sheet.Cell(i + 2, 6).Value = x.Iban; sheet.Cell(i + 2, 7).Value = x.ReceivedAtUtc.LocalDateTime; sheet.Cell(i + 2, 8).Value = x.ProcessingStatus == ProcessingStatus.ResolvedManually ? "دستی" : "خودکار"; }
    sheet.RightToLeft = true; sheet.Row(1).Style.Font.Bold = true; sheet.Columns().AdjustToContents(); using var stream = new MemoryStream(); workbook.SaveAs(stream); return Results.File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "secondary-accounts.xlsx");
});
app.Run();
static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
static IQueryable<InboundMessage> ApplyInboundMessageFilters(IQueryable<InboundMessage> query, SmsDbContext db, string? search, ProcessingStatus? status, IssueType? issueType, long? campaignId, DateTimeOffset? fromUtc)
{
    if (campaignId.HasValue) query = query.Where(x => db.SecondaryBankAccounts.Any(a => a.InboundMessageId == x.Id && a.CampaignId == campaignId.Value) || db.ReconciliationIssues.Any(r => r.InboundMessageId == x.Id && r.CampaignId == campaignId.Value));
    if (status == ProcessingStatus.Processed) query = query.Where(x => x.ProcessingStatus == ProcessingStatus.Processed || x.ProcessingStatus == ProcessingStatus.ResolvedManually);
    else if (status.HasValue) query = query.Where(x => x.ProcessingStatus == status.Value);
    if (issueType.HasValue) query = query.Where(x => db.ReconciliationIssues.Any(r => r.InboundMessageId == x.Id && r.Status == IssueStatus.Open && r.IssueType == issueType.Value));
    if (fromUtc.HasValue) query = query.Where(x => x.ReceivedAtUtc >= fromUtc.Value);
    if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => x.SenderMobile.Contains(term) || x.RawText.Contains(term) || x.ProviderMessageId.Contains(term)); }
    return query;
}

static async Task<(List<Person> People, int UniqueMobileCount)> FindEligibleInboundRecipients(IQueryable<InboundMessage> query, SmsDbContext db, CancellationToken token)
{
    var mobiles = await query.Select(x => x.SenderMobile).Distinct().ToListAsync(token);
    var people = (await db.People.AsNoTracking().Where(x => mobiles.Contains(x.Mobile) && x.Mobile != "").ToListAsync(token))
        .GroupBy(x => x.Mobile).Where(group => group.Count() == 1).Select(group => group.Single()).ToList();
    return (people, mobiles.Count);
}

static IQueryable<Person> FilterPeopleByRecipientStatuses(IQueryable<Person> query, SmsDbContext db, IReadOnlyCollection<CampaignRecipientStatus> statuses)
{
    var successful = statuses.Contains(CampaignRecipientStatus.Successful);
    var noReply = statuses.Contains(CampaignRecipientStatus.NoReply);
    var issueTypes = statuses.Select(status => status switch
    {
        CampaignRecipientStatus.NoIbanFound => (IssueType?)IssueType.NoIbanFound,
        CampaignRecipientStatus.InvalidIban => IssueType.InvalidIban,
        CampaignRecipientStatus.MultipleIbansFound => IssueType.MultipleIbansFound,
        CampaignRecipientStatus.IbanEqualsCurrentIban => IssueType.IbanEqualsCurrentIban,
        CampaignRecipientStatus.IbanUsedByAnotherPerson => IssueType.IbanUsedByAnotherPerson,
        CampaignRecipientStatus.MultipleDifferentReplies => IssueType.MultipleDifferentReplies,
        CampaignRecipientStatus.RequestNotActive => IssueType.RequestNotActive,
        CampaignRecipientStatus.RequestAmbiguous => IssueType.RequestAmbiguous,
        CampaignRecipientStatus.IncompleteIban => IssueType.IncompleteIban,
        CampaignRecipientStatus.BankCardNumberReceived => IssueType.BankCardNumberReceived,
        CampaignRecipientStatus.AccountHolderNameMismatch => IssueType.AccountHolderNameMismatch,
        _ => null
    }).Where(x => x.HasValue).Select(x => x!.Value).ToArray();

    return query.Where(person =>
        (noReply && !db.InboundMessages.Any(inbound => inbound.SenderMobile == person.Mobile)) ||
        db.InboundMessages
            .Where(inbound => inbound.SenderMobile == person.Mobile && !db.InboundMessages.Any(newer => newer.SenderMobile == inbound.SenderMobile && (newer.ReceivedAtUtc > inbound.ReceivedAtUtc || (newer.ReceivedAtUtc == inbound.ReceivedAtUtc && newer.Id > inbound.Id))))
            .Any(inbound =>
                (successful && (inbound.ProcessingStatus == ProcessingStatus.Processed || inbound.ProcessingStatus == ProcessingStatus.ResolvedManually)) ||
                db.ReconciliationIssues.Any(issue => issue.InboundMessageId == inbound.Id && issue.Status == IssueStatus.Open && issueTypes.Contains(issue.IssueType))));
}

static string IssueLabel(IssueType type) => type switch { IssueType.MobileNotFound => "شماره ناشناس", IssueType.MobileMatchesMultiplePeople => "موبایل تکراری", IssueType.NoIbanFound => "شبا پیدا نشد", IssueType.InvalidIban => "شبای نامعتبر", IssueType.MultipleIbansFound => "چند شبا", IssueType.IbanEqualsCurrentIban => "برابر حساب فعلی", IssueType.IbanUsedByAnotherPerson => "شبای متعلق به شخص دیگر", IssueType.MultipleDifferentReplies => "پاسخ متفاوت", IssueType.RequestNotActive => "درخواست غیرفعال", IssueType.RequestAmbiguous => "کمپین مبهم", IssueType.IncompleteIban => "شماره شبا ناقص است", IssueType.BankCardNumberReceived => "شماره کارت دریافت شده", IssueType.AccountHolderNameMismatch => "نام صاحب حساب با پذیرنده مطابقت ندارد", _ => type.ToString() };
static string? ValidateUser(RequestCreateUser request) { if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName)) return "نام کاربری و نام نمایشی الزامی است."; if (request.Password.Length < 8) return "رمز عبور باید حداقل ۸ نویسه باشد."; return null; }
static async Task SignIn(HttpContext http, AppUser user) { var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username), new("display_name", user.DisplayName), new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User") }; await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme))); }
static async Task<(bool RequiresReview, string? Reason)> CheckConvertedAccountHolderAsync(SmsDbContext db, ReconciliationIssue issue, BankCardConversionResult conversion, CancellationToken token)
{
    if (string.IsNullOrWhiteSpace(conversion.AccountHolderName)) return (false, null);
    var person = await ResolveIssuePersonAsync(db, issue, token);
    if (person is null) return (false, null);
    var expectedName = $"{person.FirstName} {person.LastName}".Trim();
    if (NamesMatch(expectedName, conversion.AccountHolderName)) return (false, null);
    var reason = $"نام صاحب حساب اعلام‌شده توسط بانک با نام پذیرنده مطابقت ندارد. نام بانک: {conversion.AccountHolderName}؛ نام پذیرنده: {expectedName}.";
    issue.IssueType = IssueType.AccountHolderNameMismatch;
    issue.ExtractedIban = conversion.Iban;
    issue.ResolutionNote = reason;
    var message = await db.InboundMessages.SingleAsync(x => x.Id == issue.InboundMessageId, token);
    message.ProcessingStatus = ProcessingStatus.ReconciliationRequired;
    message.LastError = reason;
    await db.SaveChangesAsync(token);
    return (true, reason);
}
static async Task<Person?> ResolveIssuePersonAsync(SmsDbContext db, ReconciliationIssue issue, CancellationToken token)
{
    var matchedPersonId = await db.InboundMessages.Where(x => x.Id == issue.InboundMessageId).Select(x => x.MatchedPersonId).SingleAsync(token);
    if (matchedPersonId.HasValue) return await db.People.SingleOrDefaultAsync(x => x.Id == matchedPersonId.Value, token);
    var people = await db.People.Where(x => x.Mobile == issue.SenderMobile).Take(2).ToListAsync(token);
    return people.Count == 1 ? people[0] : null;
}
static bool NamesMatch(string expected, string actual)
{
    return PersonNameMatcher.Matches(expected, actual);
}
public sealed record PersonInput(string NationalCode, string FirstName, string LastName, string Mobile, string? CurrentIban);
public sealed record RequestCreateCampaign(string Name, string MessageTemplate, DateTimeOffset? EndsAtUtc = null, bool ConfirmationEnabled = false, string? ConfirmationTemplate = null, bool InvalidReplyEnabled = false, string? InvalidReplyTemplate = null, bool SendToAll = true, IReadOnlyList<long>? RecipientIds = null, IReadOnlyList<CampaignRecipientStatus>? RecipientStatuses = null);
public sealed record RequestUpdateCampaign(string Name, string MessageTemplate, DateTimeOffset? EndsAtUtc = null, bool ConfirmationEnabled = false, string? ConfirmationTemplate = null, bool InvalidReplyEnabled = false, string? InvalidReplyTemplate = null);
public sealed record ResponseCampaignSummary(long Id, string Name, string MessageTemplate, bool ConfirmationEnabled, string? ConfirmationTemplate, bool InvalidReplyEnabled, string? InvalidReplyTemplate, CampaignStatus Status, DateTimeOffset StartsAtUtc, DateTimeOffset? EndsAtUtc, int RecipientCount, int SentCount, int FailedCount);
public sealed record ResponseCampaignSearch(IReadOnlyList<ResponseCampaignSummary> Items, int Total, int Page, int PageSize);
public sealed record ResponseLatestCampaignStart(DateTimeOffset? StartsAtUtc);
public sealed record ResponseUpdateCampaign(long Id, CampaignStatus Status);
public sealed record ResponseStartCampaign(long Id, CampaignStatus Status);
public sealed record ResponseReprocessInboundMessages(int Examined, int Processed, int StillNeedsReview);
public sealed record RequestBulkInboundMessage(string? Name, string? Text, string? Search, ProcessingStatus? Status, IssueType? IssueType, long? CampaignId, DateTimeOffset? FromUtc);
public sealed record ResponseBulkInboundRecipientCount(int MessageCount, int UniqueMobileCount, int RecipientCount, int SkippedCount);
public sealed record ResponseBulkInboundMessage(long CampaignId, int RecipientCount, int SkippedCount);
public sealed record RequestFinalizeReconciliationIssue(string? Iban, string? Note);
public sealed record ResponseFinalizeReconciliationIssue(long IssueId, long PersonId, long CampaignId, string Iban, int ResolvedIssueCount);
public sealed record ResponseStartCardConversion(string SessionId, string CaptchaImage, DateTimeOffset ExpiresAtUtc, string BankName, string CardLast4, string? Iban = null, string? CaptchaHint = null, string? AccountHolderName = null, bool RequiresReview = false, string? ReviewReason = null);
public sealed record RequestCompleteCardConversion(string? SessionId, string? Captcha);
public sealed record ResponseCompleteCardConversion(string Iban, string? AccountHolderName, bool RequiresReview, string? ReviewReason);
public sealed record RequestSendReconciliationIssueMessage(string? Text);
public sealed record ResponseSendReconciliationIssueMessage(long Id, OutboundStatus Status, string? ProviderReference);
public sealed record ResponseReconciliationIssue(long Id, long InboundMessageId, string SenderMobile, string? ExtractedIban, IssueType IssueType, DateTimeOffset CreatedAtUtc, long? CampaignId, string? CampaignName, long? PersonId, string? FullName, string? CurrentRegisteredIban, string? RawText, DateTimeOffset? ReceivedAtUtc, bool CanFinalize);
public sealed record ResponseFailedOutboundMessage(long Id, string FullName, string Mobile, int AttemptCount, string Error);
public sealed record ResponseFailedOutboundMessageSearch(IReadOnlyList<ResponseFailedOutboundMessage> Items, int Total, int Page, int PageSize);
public sealed record ResponsePreviousInboundReply(long Id, string RawText, DateTimeOffset ReceivedAtUtc, string? Iban);
public sealed record ResponseInboundMessage(long Id, string ProviderMessageId, string SenderMobile, string RawText, DateTimeOffset ReceivedAtUtc, ProcessingStatus ProcessingStatus, string? LastError, string? Iban, IssueType? IssueType, string? FullName, string? CurrentIban, string? CurrentBank, long? CampaignId, string? CampaignName, long? IssueId, string? CurrentRegisteredIban, bool? OwnershipVerified, bool CanFinalize, IReadOnlyList<ResponsePreviousInboundReply> PreviousReplies);
public sealed record ResponseDashboard(int PeopleCount, int ActiveCampaignCount, int SentCount, int ReceivedIbanCount, int OpenIssueCount);
public sealed record RequestLogin(string Username, string Password);
public sealed record RequestCreateUser(string Username, string DisplayName, string Password, bool IsAdmin = false);
public sealed record RequestUserStatus(bool IsActive);
public sealed record RequestResetPassword(string Password);
public sealed record RequestCreateMerchant(string TerminalNumber, string NationalId, string MerchantName, string? Mobile, string? Status, string? CurrentIban, string? Bank, string? Agency);
public partial class Program;
