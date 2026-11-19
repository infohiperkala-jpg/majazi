using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.RateLimiting;
using Majazi.Api;
using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using System.IO;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

var leadStages = new HashSet<string>(["New", "Qualified", "Proposal", "Negotiation", "Won", "Lost"]);
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");
var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be configured with at least 32 bytes.");
var issuer = builder.Configuration["Jwt:Issuer"] ?? "majazi-api";
var audience = builder.Configuration["Jwt:Audience"] ?? "majazi-web";

builder.Services.AddDbContext<MajaziDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDataProtection()
    .SetApplicationName("MAJAZI")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ?? "/var/lib/majazi/keys"));
builder.Services.AddHttpClient("TelegramBotApi", client => client.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() => CreateEgressHandler(
        builder.Configuration["Network:TelegramProxy"] ?? builder.Configuration["Network:InternationalDefaultProxy"]));
builder.Services.AddHttpClient("MetaOAuth", client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => CreateEgressHandler(
        builder.Configuration["Network:MetaProxy"] ?? builder.Configuration["Network:InternationalDefaultProxy"]));
builder.Services.AddHttpClient("AIProvider", client => client.Timeout = TimeSpan.FromSeconds(90))
    .ConfigurePrimaryHttpMessageHandler(() => CreateEgressHandler(
        builder.Configuration["Network:AIProxy"] ?? builder.Configuration["Network:InternationalDefaultProxy"]));
builder.Services.AddHostedService<ContentPublishingWorker>();
builder.Services.AddHostedService<GrowthAgentWorker>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = issuer,
            ValidateAudience = true, ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("auth", limiter =>
{
    limiter.PermitLimit = 60;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
    limiter.AutoReplenishment = true;
}));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"])
        .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MajaziDbContext>();
    await db.Database.MigrateAsync();
}

app.MapHealthChecks("/health");
app.MapGet("/api", () => Results.Ok(new { name = "MAJAZI API", version = "0.3.0", status = "running" }));

app.MapGet("/api/auth/captcha", (IMemoryCache cache) =>
{
    var answer = RandomNumberGenerator.GetInt32(0, 100000).ToString("D5");
    var id = Guid.NewGuid().ToString("N");
    cache.Set("register-captcha:" + id, answer, TimeSpan.FromMinutes(4));
    var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"220\" height=\"76\" viewBox=\"0 0 220 76\"><rect width=\"220\" height=\"76\" rx=\"10\" fill=\"#f1efff\"/><path d=\"M8 18L210 59M12 65L205 12M5 42L215 32\" stroke=\"#aaa1df\" stroke-width=\"2\" opacity=\".7\"/><text x=\"110\" y=\"52\" text-anchor=\"middle\" font-family=\"Arial,sans-serif\" font-size=\"38\" font-weight=\"700\" letter-spacing=\"9\" fill=\"#342b78\">{answer}</text></svg>";
    var imageDataUrl = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    return Results.Ok(new { captchaId = id, imageDataUrl });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/register", async (RegisterRequest request, MajaziDbContext db,
    IPasswordHasher<AppUser> hasher, IConfiguration config, IMemoryCache cache, CancellationToken ct) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    var password = request.Password;
    var orgName = request.OrganizationName?.Trim();
    var captchaId = request.CaptchaId?.Trim();
    var captchaAnswer = request.CaptchaAnswer?.Trim();
    if (string.IsNullOrWhiteSpace(captchaId) || !cache.TryGetValue("register-captcha:" + captchaId, out string? expectedCaptcha))
        return Results.BadRequest(new { error = "کد امنیتی منقضی شده است؛ تصویر جدید دریافت کنید." });
    cache.Remove("register-captcha:" + captchaId);
    var suppliedBytes = Encoding.UTF8.GetBytes(captchaAnswer ?? "");
    var expectedBytes = Encoding.UTF8.GetBytes(expectedCaptcha ?? "");
    if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        return Results.BadRequest(new { error = "کد امنیتی صحیح نیست؛ دوباره تلاش کنید." });
    var orgSlug = "org-" + Guid.NewGuid().ToString("N")[..12];
    if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || !email.Contains('@') ||
        string.IsNullOrWhiteSpace(password) || password.Length < 12 ||
        password.Length > 256 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) ||
        string.IsNullOrWhiteSpace(orgName) || orgName.Length > 180)
        return Results.BadRequest(new { error = "اطلاعات نامعتبر است. گذرواژه باید حداقل ۱۲ نویسه و شامل حروف کوچک، بزرگ و عدد باشد." });

    if (await db.Users.AnyAsync(x => x.Email == email, ct))
        return Results.Conflict(new { error = "ثبت‌نام با این ایمیل امکان‌پذیر نیست." });
    while (await db.Organizations.AnyAsync(x => x.Slug == orgSlug, ct))
        orgSlug = "org-" + Guid.NewGuid().ToString("N")[..12];

    var user = new AppUser { Email = email, PasswordHash = "pending" };
    user.PasswordHash = hasher.HashPassword(user, password);
    var organization = new Organization { Name = orgName, Slug = orgSlug };
    db.Users.Add(user);
    db.Organizations.Add(organization);
    db.OrganizationMemberships.Add(new OrganizationMembership
    {
        UserId = user.Id, OrganizationId = organization.Id, Role = "Owner"
    });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "ایمیل یا شناسه سازمان قبلاً ثبت شده است." }); }
    return Results.Ok(IssueToken(user, config));
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (LoginRequest request, MajaziDbContext db,
    IPasswordHasher<AppUser> hasher, IConfiguration config, IMemoryCache cache, CancellationToken ct) =>
{
    var captchaId = request.CaptchaId?.Trim();
    var captchaAnswer = request.CaptchaAnswer?.Trim();
    if (string.IsNullOrWhiteSpace(captchaId) || !cache.TryGetValue("register-captcha:" + captchaId, out string? expectedCaptcha))
        return Results.BadRequest(new { error = "کد امنیتی منقضی شده است؛ تصویر جدید دریافت کنید." });
    cache.Remove("register-captcha:" + captchaId);
    var suppliedBytes = Encoding.UTF8.GetBytes(captchaAnswer ?? "");
    var expectedBytes = Encoding.UTF8.GetBytes(expectedCaptcha ?? "");
    if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        return Results.BadRequest(new { error = "کد امنیتی صحیح نیست؛ دوباره تلاش کنید." });

    var email = request.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.Unauthorized();
    var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email && x.IsActive, ct);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();
    return Results.Ok(IssueToken(user, config));
}).RequireRateLimiting("auth");

app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var user = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.IsActive)
        .Select(x => new { x.Id, x.Email, x.CreatedAt }).SingleOrDefaultAsync(ct);
    if (user is null) return Results.Unauthorized();
    var organizations = await db.OrganizationMemberships.AsNoTracking()
        .Where(x => x.UserId == userId)
        .Select(x => new { x.OrganizationId, x.Role, OrganizationName = x.Organization!.Name })
        .ToListAsync(ct);
    return Results.Ok(new { user, organizations });
}).RequireAuthorization();

var organizationsApi = app.MapGroup("/api/organizations").RequireAuthorization();

organizationsApi.MapGet("/", async (ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var items = await db.OrganizationMemberships.AsNoTracking().Where(x => x.UserId == userId)
        .OrderBy(x => x.Organization!.Name)
        .Select(x => new { x.Organization!.Id, x.Organization.Name, x.Organization.Slug, x.Organization.CreatedAt, x.Role })
        .ToListAsync(ct);
    return Results.Ok(items);
});

organizationsApi.MapPost("/", async (CreateOrganizationRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var name = request.Name?.Trim();
    var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug))
        return Results.BadRequest(new { error = "نام و شناسه سازمان الزامی است." });
    if (name.Length > 180 || slug.Length > 100)
        return Results.BadRequest(new { error = "طول نام یا شناسه سازمان بیش از حد مجاز است." });
    if (await db.Organizations.AnyAsync(x => x.Slug == slug, ct))
        return Results.Conflict(new { error = "این شناسه سازمان قبلاً ثبت شده است." });
    var item = new Organization { Name = name, Slug = slug };
    db.Organizations.Add(item);
    db.OrganizationMemberships.Add(new OrganizationMembership { UserId = userId.Value, OrganizationId = item.Id, Role = "Owner" });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "شناسه سازمان تکراری است." }); }
    return Results.Created($"/api/organizations/{item.Id}", new { item.Id, item.Name, item.Slug, item.CreatedAt });
});

organizationsApi.MapPut("/{id:guid}", async (Guid id, CreateOrganizationRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.Organizations.FindAsync([id], ct);
    if (item is null) return Results.NotFound();
    var name = request.Name?.Trim(); var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug)) return Results.BadRequest(new { error = "نام و شناسه سازمان الزامی است." });
    if (name.Length > 180 || slug.Length > 100) return Results.BadRequest(new { error = "طول نام یا شناسه سازمان بیش از حد مجاز است." });
    if (await db.Organizations.AnyAsync(x => x.Id != id && x.Slug == slug, ct)) return Results.Conflict(new { error = "این شناسه سازمان قبلاً ثبت شده است." });
    item.Name = name; item.Slug = slug;
    try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Results.Conflict(new { error = "شناسه سازمان تکراری است." }); }
    return Results.Ok(new { item.Id, item.Name, item.Slug, item.CreatedAt });
});

organizationsApi.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role != "Owner") return Results.Forbid();
    var item = await db.Organizations.FindAsync([id], ct);
    if (item is null) return Results.NotFound();
    db.Organizations.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

organizationsApi.MapGet("/{id:guid}/members", async (Guid id, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    if (!await IsMember(db, userId.Value, id, ct)) return Results.NotFound();
    var members = await db.OrganizationMemberships.AsNoTracking().Where(x => x.OrganizationId == id)
        .OrderBy(x => x.User!.Email)
        .Select(x => new { userId = x.UserId, email = x.User!.Email, x.Role, x.CreatedAt })
        .ToListAsync(ct);
    return Results.Ok(members);
});

organizationsApi.MapPost("/{id:guid}/members", async (Guid id, AddMemberRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var actor = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (actor is null) return Results.NotFound();
    if (actor.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var email = request.Email?.Trim().ToLowerInvariant();
    var role = request.Role?.Trim();
    if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || role is not ("Admin" or "Member"))
        return Results.BadRequest(new { error = "ایمیل معتبر و نقش Admin یا Member الزامی است." });
    var target = await db.Users.SingleOrDefaultAsync(x => x.Email == email && x.IsActive, ct);
    if (target is null) return Results.NotFound(new { error = "کاربری با این ایمیل پیدا نشد؛ ابتدا باید ثبت‌نام کند." });
    if (await db.OrganizationMemberships.AnyAsync(x => x.UserId == target.Id && x.OrganizationId == id, ct))
        return Results.Conflict(new { error = "این کاربر عضو سازمان است." });
    db.OrganizationMemberships.Add(new OrganizationMembership { UserId = target.Id, OrganizationId = id, Role = role });
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{id}/members", new { userId = target.Id, email = target.Email, role });
});

organizationsApi.MapDelete("/{id:guid}/members/{memberId:guid}", async (Guid id, Guid memberId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var actor = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (actor is null) return Results.NotFound();
    if (actor.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var target = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == memberId && x.OrganizationId == id, ct);
    if (target is null) return Results.NotFound();
    if (target.Role == "Owner" || (actor.Role != "Owner" && target.Role == "Admin"))
        return Results.Forbid();
    db.OrganizationMemberships.Remove(target);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

organizationsApi.MapGet("/{id:guid}/brands", async (Guid id, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    if (!await IsMember(db, userId.Value, id, ct)) return Results.NotFound();
    var items = await db.Brands.AsNoTracking().Where(x => x.OrganizationId == id).OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.OrganizationId, x.Name, x.Slug, x.CreatedAt }).ToListAsync(ct);
    return Results.Ok(items);
});

organizationsApi.MapPost("/{id:guid}/brands", async (Guid id, CreateBrandRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var name = request.Name?.Trim(); var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug)) return Results.BadRequest(new { error = "نام و شناسه برند الزامی است." });
    if (name.Length > 180 || slug.Length > 100) return Results.BadRequest(new { error = "طول نام یا شناسه برند بیش از حد مجاز است." });
    if (await db.Brands.AnyAsync(x => x.OrganizationId == id && x.Slug == slug, ct)) return Results.Conflict(new { error = "این شناسه برند قبلاً ثبت شده است." });
    var item = new Brand { OrganizationId = id, Name = name, Slug = slug };
    db.Brands.Add(item);
    try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Results.Conflict(new { error = "شناسه برند تکراری است." }); }
    return Results.Created($"/api/brands/{item.Id}", new { item.Id, item.OrganizationId, item.Name, item.Slug, item.CreatedAt });
});

app.MapPut("/api/brands/{id:guid}", async (Guid id, CreateBrandRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var item = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == item.OrganizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var name = request.Name?.Trim(); var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug)) return Results.BadRequest(new { error = "نام و شناسه برند الزامی است." });
    if (name.Length > 180 || slug.Length > 100) return Results.BadRequest(new { error = "طول نام یا شناسه برند بیش از حد مجاز است." });
    if (await db.Brands.AnyAsync(x => x.Id != id && x.OrganizationId == item.OrganizationId && x.Slug == slug, ct)) return Results.Conflict(new { error = "این شناسه برند قبلاً در سازمان ثبت شده است." });
    item.Name = name; item.Slug = slug;
    try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Results.Conflict(new { error = "شناسه برند تکراری است." }); }
    return Results.Ok(new { item.Id, item.OrganizationId, item.Name, item.Slug, item.CreatedAt });
});

app.MapDelete("/api/brands/{id:guid}", async (Guid id, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var item = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == item.OrganizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    db.Brands.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});


var crmApi = app.MapGroup("/api/organizations/{organizationId:guid}/crm").RequireAuthorization();

crmApi.MapGet("/contacts", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, string? q, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var query = db.CrmContacts.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        query = query.Where(x => x.FullName.Contains(term) || (x.Email != null && x.Email.Contains(term)) || (x.Phone != null && x.Phone.Contains(term)) || (x.Company != null && x.Company.Contains(term)));
    }
    var items = await query.OrderByDescending(x => x.CreatedAt).Take(250)
        .Select(x => new { x.Id, x.BrandId, x.FullName, x.Email, x.Phone, x.Company, x.Notes, x.CreatedAt }).ToListAsync(ct);
    return Results.Ok(items);
});

crmApi.MapPost("/contacts", async (Guid organizationId, CrmContactRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var name = request.FullName?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
        (request.Email?.Length ?? 0) > 320 || (request.Phone?.Length ?? 0) > 40 ||
        (request.Company?.Length ?? 0) > 180 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "نام مخاطب الزامی است و طول اطلاعات باید معتبر باشد." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    var item = new CrmContact { OrganizationId = organizationId, BrandId = request.BrandId, FullName = name,
        Email = Clean(request.Email), Phone = Clean(request.Phone), Company = Clean(request.Company), Notes = Clean(request.Notes) };
    db.CrmContacts.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/crm/contacts/{item.Id}",
        new { item.Id, item.BrandId, item.FullName, item.Email, item.Phone, item.Company, item.Notes, item.CreatedAt });
});

crmApi.MapPut("/contacts/{contactId:guid}", async (Guid organizationId, Guid contactId, CrmContactRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.CrmContacts.SingleOrDefaultAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    var name = request.FullName?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
        (request.Email?.Length ?? 0) > 320 || (request.Phone?.Length ?? 0) > 40 ||
        (request.Company?.Length ?? 0) > 180 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "نام مخاطب الزامی است و طول اطلاعات باید معتبر باشد." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    item.FullName = name; item.BrandId = request.BrandId; item.Email = Clean(request.Email);
    item.Phone = Clean(request.Phone); item.Company = Clean(request.Company); item.Notes = Clean(request.Notes);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.BrandId, item.FullName, item.Email, item.Phone, item.Company, item.Notes, item.CreatedAt });
});

crmApi.MapDelete("/contacts/{contactId:guid}", async (Guid organizationId, Guid contactId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.CrmContacts.SingleOrDefaultAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    db.CrmContacts.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

crmApi.MapGet("/leads", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, string? stage, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var query = db.CrmLeads.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(x => x.Stage == stage);
    var items = await query.OrderByDescending(x => x.UpdatedAt).Take(250)
        .Select(x => new { x.Id, x.BrandId, x.ContactId, x.Title, x.Stage, x.EstimatedValue, x.Source, x.Notes, x.CreatedAt, x.UpdatedAt,
            ContactName = x.Contact == null ? null : x.Contact.FullName }).ToListAsync(ct);
    return Results.Ok(items);
});

crmApi.MapPost("/leads", async (Guid organizationId, CrmLeadRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var title = request.Title?.Trim();
    var stage = request.Stage?.Trim() ?? "New";
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || !leadStages.Contains(stage) ||
        request.EstimatedValue < 0 || (request.Source?.Length ?? 0) > 120 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "عنوان، مرحله یا مبلغ فرصت فروش معتبر نیست." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    if (request.ContactId is Guid contactId && !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    var item = new CrmLead { OrganizationId = organizationId, BrandId = request.BrandId, ContactId = request.ContactId,
        Title = title, Stage = stage, EstimatedValue = request.EstimatedValue, Source = Clean(request.Source), Notes = Clean(request.Notes) };
    db.CrmLeads.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/crm/leads/{item.Id}", new { item.Id, item.Title, item.Stage, item.EstimatedValue, item.ContactId, item.BrandId, item.Source, item.Notes, item.CreatedAt, item.UpdatedAt });
});

crmApi.MapPut("/leads/{leadId:guid}", async (Guid organizationId, Guid leadId, CrmLeadRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.CrmLeads.SingleOrDefaultAsync(x => x.Id == leadId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    var title = request.Title?.Trim();
    var stage = request.Stage?.Trim() ?? item.Stage;
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || !leadStages.Contains(stage) ||
        request.EstimatedValue < 0 || (request.Source?.Length ?? 0) > 120 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "عنوان، مرحله یا مبلغ فرصت فروش معتبر نیست." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    if (request.ContactId is Guid contactId && !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    item.Title = title; item.Stage = stage; item.BrandId = request.BrandId; item.ContactId = request.ContactId;
    item.EstimatedValue = request.EstimatedValue; item.Source = Clean(request.Source); item.Notes = Clean(request.Notes);
    item.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Title, item.Stage, item.EstimatedValue, item.ContactId, item.BrandId, item.Source, item.Notes, item.UpdatedAt });
});

crmApi.MapDelete("/leads/{leadId:guid}", async (Guid organizationId, Guid leadId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.CrmLeads.SingleOrDefaultAsync(x => x.Id == leadId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    db.CrmLeads.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

crmApi.MapPatch("/leads/{leadId:guid}/stage", async (Guid organizationId, Guid leadId, UpdateLeadStageRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    if (request.Stage is null || !leadStages.Contains(request.Stage)) return Results.BadRequest(new { error = "مرحله فروش معتبر نیست." });
    var item = await db.CrmLeads.SingleOrDefaultAsync(x => x.Id == leadId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    item.Stage = request.Stage;
    item.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Stage, item.UpdatedAt });
});

crmApi.MapGet("/activities", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var items = await db.CrmActivities.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .OrderByDescending(x => x.OccurredAt).Take(100)
        .Select(x => new { x.Id, x.ContactId, x.LeadId, x.ActivityType, x.Summary, x.OccurredAt, x.CreatedByUserId, ContactName = x.Contact == null ? null : x.Contact.FullName, LeadTitle = x.Lead == null ? null : x.Lead.Title }).ToListAsync(ct);
    return Results.Ok(items);
});

crmApi.MapPost("/activities", async (Guid organizationId, CrmActivityRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var type = request.ActivityType?.Trim();
    var summary = request.Summary?.Trim();
    if (type is not ("Note" or "Call" or "Email" or "Meeting" or "Task") || string.IsNullOrWhiteSpace(summary) || summary.Length > 1000)
        return Results.BadRequest(new { error = "نوع و شرح فعالیت معتبر نیست." });
    if (request.ContactId is Guid contactId && !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    if (request.LeadId is Guid leadId && !await db.CrmLeads.AnyAsync(x => x.Id == leadId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "فرصت فروش انتخاب‌شده متعلق به این سازمان نیست." });
    var item = new CrmActivity { OrganizationId = organizationId, ContactId = request.ContactId, LeadId = request.LeadId,
        CreatedByUserId = userId.Value, ActivityType = type, Summary = summary, OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow };
    db.CrmActivities.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/crm/activities/{item.Id}", new { item.Id, item.ContactId, item.LeadId, item.ActivityType, item.Summary, item.OccurredAt, item.CreatedByUserId });
});


var campaignStatuses = new HashSet<string>(["Draft", "Scheduled", "Active", "Paused", "Completed", "Cancelled"]);
var campaignChannels = new HashSet<string>(["Website", "Instagram", "Telegram", "Email", "SMS", "Ads", "Offline", "Other"]);
var campaignsApi = app.MapGroup("/api/organizations/{organizationId:guid}/campaigns").RequireAuthorization();

campaignsApi.MapGet("/", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var items = await db.MarketingCampaigns.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .OrderByDescending(x => x.UpdatedAt)
        .Select(x => new { x.Id, x.BrandId, x.Name, x.Objective, x.Channel, x.Status, x.Budget, x.ActualSpend, x.AttributedRevenue, x.Impressions, x.Clicks, x.Conversions, x.StartsAt, x.EndsAt, x.Notes, x.CreatedAt, x.UpdatedAt, ClickThroughRate = x.Impressions == 0 ? 0 : Math.Round((double)x.Clicks * 100 / x.Impressions, 2), ConversionRate = x.Clicks == 0 ? 0 : Math.Round((double)x.Conversions * 100 / x.Clicks, 2), ReturnOnAdSpend = x.ActualSpend == 0 ? 0 : Math.Round((double)(x.AttributedRevenue / x.ActualSpend), 2) })
        .ToListAsync(ct);
    return Results.Ok(items);
});

campaignsApi.MapPost("/", async (Guid organizationId, CampaignRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var name = request.Name?.Trim(); var channel = request.Channel?.Trim(); var status = request.Status?.Trim() ?? "Draft";
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || channel is null || !campaignChannels.Contains(channel) ||
        !campaignStatuses.Contains(status) || request.Budget < 0 || request.Budget > 9999999999999999m ||
        (request.Objective?.Length ?? 0) > 1000 || (request.Notes?.Length ?? 0) > 4000 ||
        request.ActualSpend < 0 || request.AttributedRevenue < 0 || request.Impressions < 0 || request.Clicks < 0 || request.Conversions < 0 || request.Clicks > request.Impressions || request.Conversions > request.Clicks ||
        (request.StartsAt.HasValue && request.EndsAt.HasValue && request.EndsAt < request.StartsAt))
        return Results.BadRequest(new { error = "اطلاعات کمپین، بودجه، کانال یا بازه زمانی معتبر نیست." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    var item = new MarketingCampaign { OrganizationId = organizationId, BrandId = request.BrandId, Name = name,
        Objective = Clean(request.Objective), Channel = channel, Status = status, Budget = request.Budget,
        StartsAt = request.StartsAt, EndsAt = request.EndsAt, Notes = Clean(request.Notes), ActualSpend = request.ActualSpend, AttributedRevenue = request.AttributedRevenue, Impressions = request.Impressions, Clicks = request.Clicks, Conversions = request.Conversions };
    db.MarketingCampaigns.Add(item); await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/campaigns/{item.Id}", item);
});

campaignsApi.MapPut("/{campaignId:guid}", async (Guid organizationId, Guid campaignId, CampaignRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.MarketingCampaigns.SingleOrDefaultAsync(x => x.Id == campaignId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    var name = request.Name?.Trim(); var channel = request.Channel?.Trim(); var status = request.Status?.Trim() ?? item.Status;
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || channel is null || !campaignChannels.Contains(channel) ||
        !campaignStatuses.Contains(status) || request.Budget < 0 || request.Budget > 9999999999999999m ||
        (request.Objective?.Length ?? 0) > 1000 || (request.Notes?.Length ?? 0) > 4000 ||
        request.ActualSpend < 0 || request.AttributedRevenue < 0 || request.Impressions < 0 || request.Clicks < 0 || request.Conversions < 0 || request.Clicks > request.Impressions || request.Conversions > request.Clicks ||
        (request.StartsAt.HasValue && request.EndsAt.HasValue && request.EndsAt < request.StartsAt))
        return Results.BadRequest(new { error = "اطلاعات کمپین، بودجه، کانال یا بازه زمانی معتبر نیست." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    item.Name = name; item.Objective = Clean(request.Objective); item.Channel = channel; item.Status = status;
    item.BrandId = request.BrandId; item.Budget = request.Budget; item.StartsAt = request.StartsAt;
    item.EndsAt = request.EndsAt; item.Notes = Clean(request.Notes); item.ActualSpend = request.ActualSpend; item.AttributedRevenue = request.AttributedRevenue; item.Impressions = request.Impressions; item.Clicks = request.Clicks; item.Conversions = request.Conversions; item.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.BrandId, item.Name, item.Objective, item.Channel, item.Status, item.Budget, item.ActualSpend, item.AttributedRevenue, item.Impressions, item.Clicks, item.Conversions, item.StartsAt, item.EndsAt, item.Notes, item.UpdatedAt });
});

campaignsApi.MapGet("/report", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var end = to ?? DateTimeOffset.UtcNow;
    var start = from ?? end.AddDays(-365);
    if (start >= end || end - start > TimeSpan.FromDays(366))
        return Results.BadRequest(new { error = "بازه گزارش باید حداکثر ۳۶۶ روز و دارای ترتیب معتبر باشد." });

    var campaigns = await db.MarketingCampaigns.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < end)
        .Select(x => new { x.Name, x.Channel, x.Status, x.Budget, x.ActualSpend, x.AttributedRevenue, x.Impressions, x.Clicks, x.Conversions })
        .ToListAsync(ct);

    var spend = campaigns.Sum(x => x.ActualSpend);
    var revenue = campaigns.Sum(x => x.AttributedRevenue);
    var impressions = campaigns.Sum(x => x.Impressions);
    var clicks = campaigns.Sum(x => x.Clicks);
    var conversions = campaigns.Sum(x => x.Conversions);
    return Results.Ok(new
    {
        from = start,
        to = end,
        campaignCount = campaigns.Count,
        budget = campaigns.Sum(x => x.Budget),
        spend,
        attributedRevenue = revenue,
        impressions,
        clicks,
        conversions,
        clickThroughRate = impressions == 0 ? 0 : Math.Round((double)clicks * 100 / impressions, 2),
        conversionRate = clicks == 0 ? 0 : Math.Round((double)conversions * 100 / clicks, 2),
        returnOnAdSpend = spend == 0 ? 0 : Math.Round((double)(revenue / spend), 2),
        byChannel = campaigns.GroupBy(x => x.Channel).Select(g => new
        {
            channel = g.Key, campaigns = g.Count(), spend = g.Sum(x => x.ActualSpend),
            revenue = g.Sum(x => x.AttributedRevenue), impressions = g.Sum(x => x.Impressions),
            clicks = g.Sum(x => x.Clicks), conversions = g.Sum(x => x.Conversions),
            roas = g.Sum(x => x.ActualSpend) == 0 ? 0 : Math.Round((double)(g.Sum(x => x.AttributedRevenue) / g.Sum(x => x.ActualSpend)), 2)
        }).OrderByDescending(x => x.spend).ToList(),
        byStatus = campaigns.GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList(),
        topCampaigns = campaigns.OrderByDescending(x => x.ActualSpend).Take(5).Select(x => new
        {
            x.Name, x.Channel, x.ActualSpend, x.AttributedRevenue, x.Clicks, x.Conversions,
            roas = x.ActualSpend == 0 ? 0 : Math.Round((double)(x.AttributedRevenue / x.ActualSpend), 2)
        }).ToList()
    });
});

campaignsApi.MapDelete("/{campaignId:guid}", async (Guid organizationId, Guid campaignId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.MarketingCampaigns.SingleOrDefaultAsync(x => x.Id == campaignId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    db.MarketingCampaigns.Remove(item); await db.SaveChangesAsync(ct); return Results.NoContent();
});



var channelsApi = app.MapGroup("/api/organizations/{organizationId:guid}/channels").RequireAuthorization();

// Instagram Business Login: state is random, short-lived, one-time, and bound to the initiating user and organization.
channelsApi.MapGet("/meta/configuration", (IConfiguration config) =>
{
    var ready = !string.IsNullOrWhiteSpace(config["Meta:AppId"]) &&
        !string.IsNullOrWhiteSpace(config["Meta:AppSecret"]) &&
        Uri.TryCreate(config["Meta:RedirectUri"], UriKind.Absolute, out var redirect) &&
        redirect.Scheme == Uri.UriSchemeHttps;
    return Results.Ok(new { instagramBusinessLoginReady = ready, redirectUriConfigured = ready });
});
channelsApi.MapGet("/meta/connect", async (Guid organizationId, Guid? brandId, string? displayName,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IMemoryCache cache, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var appId = config["Meta:AppId"];
    var redirectUri = config["Meta:RedirectUri"];
    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(config["Meta:AppSecret"]) ||
        !Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != Uri.UriSchemeHttps)
        return Results.Problem("تنظیمات Meta کامل نیست؛ App ID، App Secret و آدرس HTTPS بازگشت را در تنظیمات سرور وارد کنید.", statusCode: 503);
    if (brandId is Guid selectedBrand && !await db.Brands.AnyAsync(x => x.Id == selectedBrand && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    cache.Set("meta-oauth:" + state, new MetaOAuthState(userId.Value, organizationId, brandId,
        string.IsNullOrWhiteSpace(displayName) ? "Instagram Business" : displayName.Trim()[..Math.Min(displayName.Trim().Length, 180)]),
        TimeSpan.FromMinutes(10));
    var query = new Dictionary<string, string?>
    {
        ["client_id"] = appId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
        ["scope"] = "instagram_business_basic,instagram_business_content_publish",
        ["state"] = state
    };
    var authorizeUrl = QueryHelpers.AddQueryString("https://www.instagram.com/oauth/authorize", query);
    return Results.Ok(new { authorizationUrl = authorizeUrl });
});

app.MapGet("/api/meta/oauth/callback", async (string? code, string? state, string? error,
    string? error_description, IMemoryCache cache, IConfiguration config, IHttpClientFactory clients,
    MajaziDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(state) || !cache.TryGetValue("meta-oauth:" + state, out MetaOAuthState? flow) || flow is null)
        return Results.BadRequest("درخواست اتصال نامعتبر یا منقضی شده است. دوباره از پنل شروع کنید.");
    cache.Remove("meta-oauth:" + state);
    if (!string.IsNullOrWhiteSpace(error))
        return Results.BadRequest("اتصال توسط کاربر لغو شد یا مجوز لازم صادر نشد.");
    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest("کد تأیید از Meta دریافت نشد.");
    if (!await IsMember(db, flow.UserId, flow.OrganizationId, ct))
        return Results.Forbid();
    var appId = config["Meta:AppId"];
    var appSecret = config["Meta:AppSecret"];
    var redirectUri = config["Meta:RedirectUri"];
    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(redirectUri))
        return Results.Problem("تنظیمات اتصال Meta روی سرور کامل نیست.", statusCode: 503);

    try
    {
        var client = clients.CreateClient("MetaOAuth");
        using var exchange = await client.PostAsync("https://api.instagram.com/oauth/access_token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = appId, ["client_secret"] = appSecret, ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri, ["code"] = code
            }), ct);
        if (!exchange.IsSuccessStatusCode)
            return Results.BadRequest("Meta کد مجوز را نپذیرفت. تنظیمات برنامه و Redirect URI را بررسی کنید.");
        using var exchangeJson = await JsonDocument.ParseAsync(await exchange.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var shortToken = exchangeJson.RootElement.TryGetProperty("access_token", out var tokenNode) ? tokenNode.GetString() : null;
        var externalId = exchangeJson.RootElement.TryGetProperty("user_id", out var idNode) ? idNode.ToString() : null;
        if (string.IsNullOrWhiteSpace(shortToken) || string.IsNullOrWhiteSpace(externalId))
            return Results.Problem("پاسخ مجوز Meta ناقص بود.", statusCode: 502);

        var longUri = QueryHelpers.AddQueryString("https://graph.instagram.com/access_token",
            new Dictionary<string, string?>
            {
                ["grant_type"] = "ig_exchange_token", ["client_secret"] = appSecret, ["access_token"] = shortToken
            });
        using var longResponse = await client.GetAsync(longUri, ct);
        if (!longResponse.IsSuccessStatusCode)
            return Results.Problem("تبدیل توکن کوتاه‌مدت به توکن بلندمدت ناموفق بود.", statusCode: 502);
        using var longJson = await JsonDocument.ParseAsync(await longResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var longToken = longJson.RootElement.TryGetProperty("access_token", out var longNode) ? longNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(longToken))
            return Results.Problem("توکن بلندمدت از Meta دریافت نشد.", statusCode: 502);

        var existing = await db.SocialChannelConnections.SingleOrDefaultAsync(x =>
            x.OrganizationId == flow.OrganizationId && x.Provider == "Instagram" && x.ExternalAccountId == externalId, ct);
        if (existing is null)
        {
            existing = new SocialChannelConnection
            {
                OrganizationId = flow.OrganizationId, BrandId = flow.BrandId, Provider = "Instagram",
                DisplayName = flow.DisplayName, ExternalAccountId = externalId
            };
            db.SocialChannelConnections.Add(existing);
        }
        else
        {
            existing.BrandId = flow.BrandId;
            existing.DisplayName = flow.DisplayName;
        }
        existing.EncryptedAccessToken = protection.CreateProtector("Majazi.SocialConnections.v1").Protect(longToken);
        existing.Status = "Connected";
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var returnUrl = config["Meta:FrontendReturnUrl"] ?? "/channels?meta=connected";
        return Results.Content("<!doctype html><html lang='fa' dir='rtl'><meta charset='utf-8'><title>اتصال موفق</title><body style='font-family:sans-serif;padding:40px;text-align:center'><h2>اتصال اینستاگرام با موفقیت تأیید شد.</h2><p>توکن به‌صورت رمز‌شده ذخیره شد.</p><a href='" + System.Net.WebUtility.HtmlEncode(returnUrl) + "'>بازگشت به پنل MAJAZI</a></body></html>", "text/html; charset=utf-8");
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("ارتباط با سرویس OAuth متا در زمان مقرر پاسخ نداد.", statusCode: 504);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("ارتباط با سرویس OAuth متا برقرار نشد.", statusCode: 502);
    }
});


var channelProviders = new HashSet<string>(["Instagram", "Telegram"]);
channelsApi.MapGet("/", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var items = await db.SocialChannelConnections.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .OrderBy(x => x.Provider).ThenBy(x => x.DisplayName)
        .Select(x => new { x.Id, x.BrandId, x.Provider, x.DisplayName, x.ExternalAccountId, x.Status, x.CreatedAt, x.UpdatedAt,
            HasCredential = x.EncryptedAccessToken != null })
        .ToListAsync(ct);
    return Results.Ok(items);
});
channelsApi.MapPost("/", async (SocialChannelRequest request, Guid organizationId, ClaimsPrincipal principal,
    MajaziDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var provider = request.Provider?.Trim();
    var displayName = request.DisplayName?.Trim();
    var externalId = Clean(request.ExternalAccountId);
    var credential = request.AccessToken?.Trim();
    if (provider is null || !channelProviders.Contains(provider) || string.IsNullOrWhiteSpace(displayName) ||
        displayName.Length > 180 || (externalId?.Length ?? 0) > 180 ||
        (credential is not null && (credential.Length < 8 || credential.Length > 8000)) ||
        (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct)))
        return Results.BadRequest(new { error = "اطلاعات کانال یا برند معتبر نیست." });
    if (externalId is not null && await db.SocialChannelConnections.AnyAsync(x => x.OrganizationId == organizationId &&
        x.Provider == provider && x.ExternalAccountId == externalId, ct))
        return Results.Conflict(new { error = "این حساب کانال قبلاً در سازمان ثبت شده است." });
    var protector = protection.CreateProtector("Majazi.SocialConnections.v1");
    var item = new SocialChannelConnection
    {
        OrganizationId = organizationId, BrandId = request.BrandId, Provider = provider,
        DisplayName = displayName, ExternalAccountId = externalId,
        EncryptedAccessToken = credential is null ? null : protector.Protect(credential),
        Status = credential is null ? "PendingAuthorization" : "CredentialsStored"
    };
    db.SocialChannelConnections.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/channels/{item.Id}",
        new { item.Id, item.BrandId, item.Provider, item.DisplayName, item.ExternalAccountId, item.Status,
            HasCredential = item.EncryptedAccessToken != null, item.CreatedAt });
});
channelsApi.MapPost("/{connectionId:guid}/webhook", async (Guid organizationId, Guid connectionId,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IDataProtectionProvider protection, IHttpClientFactory clients,
    CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.SocialChannelConnections.SingleOrDefaultAsync(
        x => x.Id == connectionId && x.OrganizationId == organizationId && x.Provider == "Telegram", ct);
    if (item is null) return Results.NotFound();
    if (item.Status != "Connected") return Results.Conflict(new { error = "ابتدا اتصال ربات را تأیید کنید." });
    var publicBaseUrl = config["PublicBaseUrl"]?.TrimEnd('/');
    if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri) ||
        (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
        return Results.BadRequest(new { error = "PublicBaseUrl باید آدرس عمومی HTTPS سامانه باشد." });
    var signature = TelegramWebhookSignature(config["Jwt:SigningKey"]!, connectionId);
    var secret = TelegramWebhookSecret(config["Jwt:SigningKey"]!, connectionId);
    var webhookUrl = $"{publicBaseUrl}/api/webhooks/telegram/{connectionId:D}/{signature}";
    string botToken;
    try { botToken = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(item.EncryptedAccessToken!); }
    catch { return Results.Conflict(new { error = "توکن ربات قابل رمزگشایی نیست؛ اتصال را دوباره برقرار کنید." }); }
    try
    {
        using var response = await clients.CreateClient("TelegramBotApi").PostAsJsonAsync(
            $"https://api.telegram.org/bot{botToken}/setWebhook",
            new { url = webhookUrl, secret_token = secret, allowed_updates = new[] { "message", "edited_message" }, drop_pending_updates = false }, ct);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!response.IsSuccessStatusCode || !payload.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            return Results.Problem("تلگرام وب‌هوک را ثبت نکرد؛ تنظیمات دامنه و دسترسی سرور را بررسی کنید.", statusCode: 502);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { configured = true, webhookUrl, provider = "Telegram" });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("ثبت وب‌هوک تلگرام به پایان زمان مجاز رسید.", statusCode: 504);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("ارتباط با Telegram Bot API برقرار نشد.", statusCode: 502);
    }
});

channelsApi.MapPost("/{connectionId:guid}/verify", async (Guid organizationId, Guid connectionId,
    ClaimsPrincipal principal, MajaziDbContext db, IDataProtectionProvider protection,
    IHttpClientFactory httpClientFactory, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.SocialChannelConnections.SingleOrDefaultAsync(
        x => x.Id == connectionId && x.OrganizationId == organizationId && x.Provider == "Telegram", ct);
    if (item is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(item.EncryptedAccessToken))
        return Results.BadRequest(new { error = "برای این کانال توکن ربات ثبت نشده است." });

    string botToken;
    try { botToken = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(item.EncryptedAccessToken); }
    catch (CryptographicException)
    {
        item.Status = "VerificationFailed";
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Conflict(new { error = "توکن ذخیره‌شده قابل رمزگشایی نیست؛ اعتبارنامه را دوباره ثبت کنید." });
    }

    try
    {
        using var response = await httpClientFactory.CreateClient("TelegramBotApi")
            .GetAsync($"https://api.telegram.org/bot{botToken}/getMe", ct);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = payload.RootElement;
        if (response.IsSuccessStatusCode && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("result", out var result) && result.TryGetProperty("id", out var botId))
        {
            var verifiedBotId = botId.ToString();
            if (await db.SocialChannelConnections.AnyAsync(x => x.Id != item.Id && x.OrganizationId == organizationId &&
                x.Provider == "Telegram" && x.ExternalAccountId == verifiedBotId, ct))
            {
                item.Status = "VerificationFailed";
                item.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Conflict(new { error = "این ربات قبلاً به‌عنوان کانال دیگری در همین سازمان ثبت شده است." });
            }
            item.ExternalAccountId = verifiedBotId;
            item.Status = "Connected";
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var username = result.TryGetProperty("username", out var userName) ? userName.GetString() : null;
            return Results.Ok(new { item.Id, item.Provider, item.DisplayName, item.ExternalAccountId, Username = username, item.Status, VerifiedAt = item.UpdatedAt });
        }
        item.Status = "VerificationFailed";
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.BadRequest(new { error = "تلگرام اعتبارنامه را تأیید نکرد. توکن ربات را بررسی کنید." });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("ارتباط با Telegram Bot API در زمان مقرر پاسخ نداد.", statusCode: 504);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("ارتباط با Telegram Bot API برقرار نشد.", statusCode: 502);
    }
});

channelsApi.MapPost("/{connectionId:guid}/health-check", async (Guid organizationId, Guid connectionId,
    ClaimsPrincipal principal, MajaziDbContext db, IDataProtectionProvider protection, IHttpClientFactory clients, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.SocialChannelConnections.SingleOrDefaultAsync(x => x.Id == connectionId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(item.EncryptedAccessToken))
        return Results.Conflict(new { error = "برای این اتصال اعتبارنامه ثبت نشده است." });
    string credential;
    try { credential = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(item.EncryptedAccessToken); }
    catch (CryptographicException) { return Results.Conflict(new { error = "اعتبارنامه رمز‌شده قابل خواندن نیست؛ اتصال را دوباره برقرار کنید." }); }
    var started = DateTimeOffset.UtcNow;
    try
    {
        string endpoint;
        if (item.Provider == "Telegram") endpoint = $"https://api.telegram.org/bot{credential}/getMe";
        else if (item.Provider == "Instagram") endpoint = QueryHelpers.AddQueryString("https://graph.instagram.com/me",
            new Dictionary<string, string?> { ["fields"] = "id,user_id,username", ["access_token"] = credential });
        else return Results.Conflict(new { error = "برای این پلتفرم هنوز آداپتور رسمی بررسی سلامت پیاده‌سازی نشده است." });
        using var response = await clients.CreateClient(item.Provider == "Telegram" ? "TelegramBotApi" : "MetaOAuth").GetAsync(endpoint, ct);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = payload.RootElement;
        var providerOk = response.IsSuccessStatusCode &&
            (item.Provider == "Telegram"
                ? root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                : root.TryGetProperty("id", out _));
        if (!providerOk)
        {
            item.Status = "VerificationFailed";
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { healthy = false, provider = item.Provider, status = item.Status,
                checkedAt = DateTimeOffset.UtcNow, latencyMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                message = "سرویس مقصد اعتبارنامه را تأیید نکرد." });
        }
        var result = root.TryGetProperty("result", out var telegramResult) ? telegramResult : root;
        if (result.TryGetProperty("id", out var externalId)) item.ExternalAccountId = externalId.ToString();
        var accountName = result.TryGetProperty("username", out var username) ? username.GetString() : item.DisplayName;
        item.Status = "Connected";
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { healthy = true, provider = item.Provider, status = item.Status,
            accountName, externalAccountId = item.ExternalAccountId, checkedAt = DateTimeOffset.UtcNow,
            latencyMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            message = "اعتبارنامه با API مقصد تأیید شد." });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Ok(new { healthy = false, provider = item.Provider, status = item.Status,
            checkedAt = DateTimeOffset.UtcNow, message = "پاسخ سرویس مقصد در مهلت مقرر دریافت نشد." });
    }
    catch (HttpRequestException)
    {
        return Results.Ok(new { healthy = false, provider = item.Provider, status = item.Status,
            checkedAt = DateTimeOffset.UtcNow, message = "ارتباط شبکه‌ای با سرویس مقصد برقرار نشد؛ مسیر خروجی و پراکسی را بررسی کنید." });
    }
});

channelsApi.MapDelete("/{connectionId:guid}", async (Guid organizationId, Guid connectionId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.SocialChannelConnections.SingleOrDefaultAsync(x => x.Id == connectionId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    db.SocialChannelConnections.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});


var dashboardApi = app.MapGroup("/api/organizations/{organizationId:guid}/dashboard").RequireAuthorization();
dashboardApi.MapGet("/summary", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct))
        return Results.NotFound();

    var contacts = await db.CrmContacts.CountAsync(x => x.OrganizationId == organizationId, ct);
    var leads = await db.CrmLeads.CountAsync(x => x.OrganizationId == organizationId, ct);
    var openLeads = await db.CrmLeads.CountAsync(x => x.OrganizationId == organizationId && x.Stage != "Won" && x.Stage != "Lost", ct);
    var campaigns = await db.MarketingCampaigns.CountAsync(x => x.OrganizationId == organizationId, ct);
    var activeCampaigns = await db.MarketingCampaigns.CountAsync(x => x.OrganizationId == organizationId && x.Status == "Active", ct);
    var connectedChannels = await db.SocialChannelConnections.CountAsync(x => x.OrganizationId == organizationId && x.Status == "Connected", ct);
    var totalChannels = await db.SocialChannelConnections.CountAsync(x => x.OrganizationId == organizationId, ct);
    var members = await db.OrganizationMemberships.CountAsync(x => x.OrganizationId == organizationId, ct);
    var campaignTotals = await db.MarketingCampaigns.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId)
        .GroupBy(_ => 1)
        .Select(g => new
        {
            Budget = g.Sum(x => x.Budget),
            Spend = g.Sum(x => x.ActualSpend),
            Revenue = g.Sum(x => x.AttributedRevenue),
            Impressions = g.Sum(x => x.Impressions),
            Clicks = g.Sum(x => x.Clicks),
            Conversions = g.Sum(x => x.Conversions)
        })
        .SingleOrDefaultAsync(ct);

    return Results.Ok(new
    {
        organizationId,
        contacts,
        leads,
        openLeads,
        campaigns,
        activeCampaigns,
        connectedChannels,
        totalChannels,
        members,
        budget = campaignTotals?.Budget ?? 0m,
        spend = campaignTotals?.Spend ?? 0m,
        attributedRevenue = campaignTotals?.Revenue ?? 0m,
        impressions = campaignTotals?.Impressions ?? 0L,
        clicks = campaignTotals?.Clicks ?? 0L,
        conversions = campaignTotals?.Conversions ?? 0L,
        updatedAt = DateTimeOffset.UtcNow
    });
});


app.MapPost("/api/webhooks/telegram/{connectionId:guid}/{signature}", async (Guid connectionId, string signature,
    HttpRequest request, MajaziDbContext db, IDataProtectionProvider protection, IConfiguration config, CancellationToken ct) =>
{
    var signingKey = config["Jwt:SigningKey"]!;
    var expectedSignature = TelegramWebhookSignature(signingKey, connectionId);
    var expectedSecret = TelegramWebhookSecret(signingKey, connectionId);
    var suppliedSecret = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature), Encoding.UTF8.GetBytes(expectedSignature)) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedSecret), Encoding.UTF8.GetBytes(expectedSecret)))
        return Results.Unauthorized();

    using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var root = document.RootElement;
    JsonElement telegramMessage;
    if (!root.TryGetProperty("message", out telegramMessage) && !root.TryGetProperty("edited_message", out telegramMessage))
        return Results.Ok(new { accepted = true, ignored = true });
    if (!telegramMessage.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatIdElement) ||
        !chatIdElement.TryGetInt64(out var chatId) || !telegramMessage.TryGetProperty("message_id", out var messageIdElement))
        return Results.BadRequest();
    var externalMessageId = messageIdElement.ToString();
    var body = telegramMessage.TryGetProperty("text", out var textElement) ? textElement.GetString() :
        telegramMessage.TryGetProperty("caption", out var captionElement) ? captionElement.GetString() : null;
    if (string.IsNullOrWhiteSpace(body)) return Results.Ok(new { accepted = true, ignored = true });
    if (body.Length > 10000) body = body[..10000];

    var channel = await db.SocialChannelConnections.SingleOrDefaultAsync(
        x => x.Id == connectionId && x.Provider == "Telegram" && x.Status == "Connected", ct);
    if (channel is null) return Results.NotFound();
    var externalChatId = chatId.ToString();
    var conversation = await db.InboxConversations.SingleOrDefaultAsync(
        x => x.ChannelConnectionId == connectionId && x.ExternalConversationId == externalChatId, ct);
    if (conversation is null)
    {
        var senderName = telegramMessage.TryGetProperty("from", out var from)
            ? string.Join(" ", new[] {
                from.TryGetProperty("first_name", out var first) ? first.GetString() : null,
                from.TryGetProperty("last_name", out var last) ? last.GetString() : null
              }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;
        conversation = new InboxConversation {
            OrganizationId = channel.OrganizationId, ChannelConnectionId = channel.Id,
            ExternalConversationId = externalChatId, Subject = string.IsNullOrWhiteSpace(senderName) ? $"Telegram {externalChatId}" : senderName,
            Status = "Open"
        };
        db.InboxConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
    }
    var duplicate = await db.InboxMessages.AnyAsync(
        x => x.ConversationId == conversation.Id && x.ExternalMessageId == externalMessageId, ct);
    if (duplicate) return Results.Ok(new { accepted = true, duplicate = true });
    var receivedAt = DateTimeOffset.UtcNow;
    if (telegramMessage.TryGetProperty("date", out var dateElement) && dateElement.TryGetInt64(out var unixSeconds))
        receivedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    db.InboxMessages.Add(new InboxMessage {
        ConversationId = conversation.Id, ExternalMessageId = externalMessageId,
        Direction = "Inbound", Body = body, Status = "Received", CreatedAt = receivedAt
    });
    conversation.LastMessageAt = receivedAt;
    conversation.UpdatedAt = DateTimeOffset.UtcNow;
    if (conversation.Status == "Resolved") conversation.Status = "Open";
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { accepted = true });
});


var contentApi = app.MapGroup("/api/organizations/{organizationId:guid}/content").RequireAuthorization();

var growthAgentApi = app.MapGroup("/api/organizations/{organizationId:guid}/growth-agent").RequireAuthorization();

growthAgentApi.MapGet("/latest-plan", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var plan = await db.ContentDrafts.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.Prompt == "برنامه روزانه خودکار عامل رشد MAJAZI")
        .OrderByDescending(x => x.CreatedAt)
        .Select(x => new { x.Id, x.Title, x.Content, x.Status, x.CreatedAt, x.AiModel })
        .FirstOrDefaultAsync(ct);
    return plan is null ? Results.NotFound() : Results.Ok(plan);
});

growthAgentApi.MapGet("/", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var org = await db.Organizations.AsNoTracking().Where(x => x.Id == organizationId)
        .Select(x => new { x.GrowthAgentEnabled, x.GrowthAgentLastRunAt }).SingleOrDefaultAsync(ct);
    if (org is null) return Results.NotFound();
    return Results.Ok(new {
        enabled = org.GrowthAgentEnabled,
        lastRunAt = org.GrowthAgentLastRunAt,
        aiConfigured = !string.IsNullOrWhiteSpace(config["AI:ApiKey"]) && !string.IsNullOrWhiteSpace(config["AI:Model"]) && Uri.TryCreate(config["AI:BaseUrl"], UriKind.Absolute, out var aiEndpoint) && (aiEndpoint.Scheme == Uri.UriSchemeHttps || aiEndpoint.IsLoopback),
        cadence = "daily",
        execution = "server-background-worker",
        autoPublishing = false,
        proxyConfigured = !string.IsNullOrWhiteSpace(config["Network:InternationalDefaultProxy"]),
        telegramProxyConfigured = !string.IsNullOrWhiteSpace(config["Network:TelegramProxy"]),
        metaProxyConfigured = !string.IsNullOrWhiteSpace(config["Network:MetaProxy"]),
        aiProxyConfigured = !string.IsNullOrWhiteSpace(config["Network:AIProxy"])
    });
});

growthAgentApi.MapPut("/", async (Guid organizationId, GrowthAgentSettingsRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var org = await db.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, ct);
    if (org is null) return Results.NotFound();
    org.GrowthAgentEnabled = request.Enabled;
    if (request.Enabled) org.GrowthAgentLastRunAt = null;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { enabled = org.GrowthAgentEnabled, message = request.Enabled
        ? "عامل رشد فعال شد؛ اولین برنامه در چرخه پس‌زمینه بعدی تولید می‌شود."
        : "عامل رشد متوقف شد؛ برنامه‌های قبلی و داده‌ها حفظ شدند." });
});


contentApi.MapGet("/ai/status", (IConfiguration config) => Results.Ok(new
{
    configured = !string.IsNullOrWhiteSpace(config["AI:ApiKey"]) &&
        Uri.TryCreate(config["AI:BaseUrl"], UriKind.Absolute, out _),
    model = config["AI:Model"] ?? "not-configured",
    provider = "OpenAI-compatible",
    keyStored = !string.IsNullOrWhiteSpace(config["AI:ApiKey"])
}));


contentApi.MapGet("/publish-targets", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    return Results.Ok(await db.SocialChannelConnections.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.Provider == "Telegram" && x.Status == "Connected")
        .Select(x => new { x.Id, x.DisplayName, x.ExternalAccountId }).ToListAsync(ct));
});

contentApi.MapPost("/drafts/{draftId:guid}/schedule", async (Guid organizationId, Guid draftId,
    ScheduleContentRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId).Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var scheduledAt = request.ScheduledAt;
    if (scheduledAt is null || scheduledAt <= DateTimeOffset.UtcNow.AddMinutes(1) || scheduledAt > DateTimeOffset.UtcNow.AddYears(1))
        return Results.BadRequest(new { error = "زمان انتشار باید حداقل یک دقیقه بعد و حداکثر تا یک سال آینده باشد." });
    if (!IsValidTelegramChatTarget(request.TargetChatId))
        return Results.BadRequest(new { error = "شناسه عددی مقصد تلگرام را وارد کنید." });
    var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == draftId && x.OrganizationId == organizationId, ct);
    if (draft is null) return Results.NotFound();
    if (draft.Content.Length > 4096) return Results.BadRequest(new { error = "متن برای ارسال تلگرام باید حداکثر ۴۰۹۶ نویسه باشد." });
    if (draft.Status != "Approved") return Results.Conflict(new { error = "ابتدا محتوا باید تأیید نهایی شود." });
    var connection = await db.SocialChannelConnections.SingleOrDefaultAsync(x =>
        x.Id == request.ConnectionId && x.OrganizationId == organizationId && x.Provider == "Telegram" && x.Status == "Connected", ct);
    if (connection is null || string.IsNullOrWhiteSpace(connection.EncryptedAccessToken))
        return Results.BadRequest(new { error = "اتصال فعال تلگرام انتخاب نشده است." });
    if (draft.Channel != "Telegram") return Results.BadRequest(new { error = "این محتوا برای کانال دیگری تعریف شده است." });
    draft.Status = "Scheduled"; draft.ScheduledAt = scheduledAt.Value.ToUniversalTime();
    draft.PublishConnectionId = connection.Id; draft.PublishTarget = request.TargetChatId.Trim(); draft.PublishError = null;
    draft.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { draft.Id, draft.Status, draft.ScheduledAt, draft.PublishTarget });
});

contentApi.MapPost("/drafts/{draftId:guid}/publish-now", async (Guid organizationId, Guid draftId,
    PublishNowRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId).Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == draftId && x.OrganizationId == organizationId, ct);
    if (draft is null) return Results.NotFound();
    if (draft.Status != "Approved" || draft.Channel != "Telegram")
        return Results.Conflict(new { error = "فقط محتوای تأییدشده تلگرام قابل انتشار است." });
    if (draft.Content.Length > 4096) return Results.BadRequest(new { error = "متن برای ارسال تلگرام باید حداکثر ۴۰۹۶ نویسه باشد." });
    if (!IsValidTelegramChatTarget(request.TargetChatId))
        return Results.BadRequest(new { error = "شناسه عددی مقصد تلگرام الزامی است." });
    var connection = await db.SocialChannelConnections.SingleOrDefaultAsync(x => x.Id == request.ConnectionId &&
        x.OrganizationId == organizationId && x.Provider == "Telegram" && x.Status == "Connected", ct);
    if (connection is null) return Results.BadRequest(new { error = "اتصال فعال تلگرام یافت نشد." });
    draft.Status = "Scheduled"; draft.ScheduledAt = DateTimeOffset.UtcNow.AddSeconds(2);
    draft.PublishConnectionId = connection.Id; draft.PublishTarget = request.TargetChatId.Trim();
    draft.PublishError = null; draft.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Accepted(value: new { draft.Id, draft.Status, message = "محتوا برای ارسال فوری در صف قرار گرفت." });
});

contentApi.MapPost("/drafts/{draftId:guid}/cancel-schedule", async (Guid organizationId, Guid draftId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId).Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == draftId && x.OrganizationId == organizationId, ct);
    if (draft is null) return Results.NotFound();
    if (draft.Status != "Scheduled") return Results.Conflict(new { error = "این محتوا در صف زمان‌بندی نیست." });
    draft.Status = "Approved"; draft.ScheduledAt = null; draft.PublishConnectionId = null; draft.PublishTarget = null;
    draft.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { draft.Id, draft.Status });
});

contentApi.MapGet("/calendar", async (Guid organizationId, ClaimsPrincipal principal,
    MajaziDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var rangeStart = from ?? DateTimeOffset.UtcNow.AddDays(-30);
    var rangeEnd = to ?? DateTimeOffset.UtcNow.AddDays(90);
    if (rangeEnd <= rangeStart || rangeEnd - rangeStart > TimeSpan.FromDays(366))
        return Results.BadRequest(new { error = "بازه تقویم باید معتبر و حداکثر ۳۶۶ روز باشد." });

    var items = await db.ContentDrafts.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId &&
            ((x.ScheduledAt >= rangeStart && x.ScheduledAt < rangeEnd) ||
             (x.PublishedAt >= rangeStart && x.PublishedAt < rangeEnd)))
        .OrderBy(x => x.ScheduledAt ?? x.PublishedAt)
        .Select(x => new
        {
            x.Id, x.Title, x.Channel, x.Status, x.ScheduledAt, x.PublishedAt,
            x.PublishTarget, x.PublishError, x.UpdatedAt
        }).Take(500).ToListAsync(ct);
    return Results.Ok(new { from = rangeStart, to = rangeEnd, items });
});

contentApi.MapGet("/drafts", async (Guid organizationId, ClaimsPrincipal principal,
    MajaziDbContext db, string? status, string? q, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var query = db.ContentDrafts.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(status) && status != "All") query = query.Where(x => x.Status == status);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        query = query.Where(x => x.Title.Contains(term) || x.Content.Contains(term));
    }
    var items = await query.OrderByDescending(x => x.UpdatedAt).Take(100)
        .Select(x => new { x.Id, x.BrandId, x.Title, x.Content, x.Channel, x.Tone, x.Status, x.AiModel,
            x.CreatedAt, x.UpdatedAt, x.ApprovedAt, x.ScheduledAt, x.PublishedAt, x.PublishError, VersionCount = x.Versions.Count })
        .ToListAsync(ct);
    return Results.Ok(items);
});

contentApi.MapPost("/generate", async (Guid organizationId, GenerateContentRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config,
    IHttpClientFactory clients, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var topic = request.Topic?.Trim();
    var channel = string.IsNullOrWhiteSpace(request.Channel) ? "General" : request.Channel.Trim();
    var tone = string.IsNullOrWhiteSpace(request.Tone) ? "Professional" : request.Tone.Trim();
    var prompt = request.Instructions?.Trim();
    if (string.IsNullOrWhiteSpace(topic) || topic.Length > 1500 || channel.Length > 40 ||
        tone.Length > 40 || (prompt?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "موضوع، لحن، کانال یا دستور تکمیلی معتبر نیست." });
    var apiKey = config["AI:ApiKey"];
    var baseUrl = config["AI:BaseUrl"]?.TrimEnd('/');
    var model = config["AI:Model"];
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl) ||
        !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
        return Results.Problem("اتصال هوش مصنوعی پیکربندی نشده است. AI__BaseUrl، AI__ApiKey و AI__Model را در تنظیمات امن سرور تنظیم کنید.", statusCode: 503);
    if (string.IsNullOrWhiteSpace(model)) return Results.Problem("مدل هوش مصنوعی مشخص نشده است.", statusCode: 503);
    var brandName = request.BrandId is Guid brandId
        ? await db.Brands.Where(x => x.Id == brandId && x.OrganizationId == organizationId).Select(x => x.Name).SingleOrDefaultAsync(ct)
        : null;
    if (request.BrandId is not null && brandName is null)
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    var systemPrompt = "شما استراتژیست ارشد محتوا و کپی‌رایتر فارسی هستید. محتوای دقیق، طبیعی، کاربردی و غیرتکراری بنویسید. ادعای آماری یا factual بدون منبع نسازید. خروجی را فقط به شکل JSON با دو کلید title و content بدهید.";
    var userPrompt = $"موضوع: {topic}\nکانال انتشار: {channel}\nلحن: {tone}\nبرند: {brandName ?? "عمومی"}\nدستور تکمیلی: {prompt ?? "ندارد"}\nمحتوا را متناسب با کانال و مخاطب فارسی‌زبان تولید کن.";
    try
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            model, temperature = 0.7,
            response_format = new { type = "json_object" },
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        });
        using var response = await clients.CreateClient("AIProvider").SendAsync(message, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return Results.Problem("سرویس هوش مصنوعی پاسخ موفق نداد (" + (int)response.StatusCode + "). تنظیمات مدل و اعتبار دسترسی را بررسی کنید.", statusCode: 502);
        using var document = JsonDocument.Parse(responseText);
        var generated = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(generated)) return Results.Problem("پاسخ مدل خالی بود.", statusCode: 502);
        using var output = JsonDocument.Parse(generated);
        var title = output.RootElement.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
        var content = output.RootElement.TryGetProperty("content", out var contentElement) ? contentElement.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(content) || content.Length > 30000)
            return Results.Problem("ساختار خروجی مدل معتبر نیست.", statusCode: 502);
        var draft = new ContentDraft
        {
            OrganizationId = organizationId, BrandId = request.BrandId, CreatedByUserId = userId.Value,
            Title = string.IsNullOrWhiteSpace(title) ? topic[..Math.Min(topic.Length, 240)] : title[..Math.Min(title.Length, 240)],
            Content = content, Channel = channel, Tone = tone, Prompt = userPrompt, AiModel = model
        };
        draft.Versions.Add(new ContentDraftVersion
        {
            DraftId = draft.Id, VersionNumber = 1, Title = draft.Title, Content = draft.Content,
            ChangeNote = "نسخه اولیه تولیدشده با هوش مصنوعی", CreatedByUserId = userId.Value
        });
        db.ContentDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/content/drafts/{draft.Id}",
            new { draft.Id, draft.Title, draft.Content, draft.Channel, draft.Tone, draft.Status, draft.AiModel, draft.CreatedAt, versionCount = 1 });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (HttpRequestException) { return Results.Problem("ارتباط با سرویس هوش مصنوعی برقرار نشد.", statusCode: 502); }
    catch (TaskCanceledException) { return Results.Problem("زمان پاسخ‌گویی هوش مصنوعی به پایان رسید.", statusCode: 504); }
    catch (JsonException) { return Results.Problem("پاسخ سرویس هوش مصنوعی قابل پردازش نبود.", statusCode: 502); }
});

contentApi.MapGet("/drafts/{draftId:guid}", async (Guid organizationId, Guid draftId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var draft = await db.ContentDrafts.AsNoTracking()
        .Where(x => x.Id == draftId && x.OrganizationId == organizationId)
        .Select(x => new { x.Id, x.BrandId, x.Title, x.Content, x.Channel, x.Tone, x.Status, x.Prompt, x.AiModel,
            x.CreatedAt, x.UpdatedAt, x.ApprovedAt, x.ScheduledAt, x.PublishedAt, x.PublishError, Versions = x.Versions.OrderByDescending(v => v.VersionNumber)
                .Select(v => new { v.Id, v.VersionNumber, v.Title, v.Content, v.ChangeNote, v.CreatedAt }).ToList() })
        .SingleOrDefaultAsync(ct);
    return draft is null ? Results.NotFound() : Results.Ok(draft);
});

contentApi.MapPut("/drafts/{draftId:guid}", async (Guid organizationId, Guid draftId,
    EditContentDraftRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var draft = await db.ContentDrafts.Include(x => x.Versions)
        .SingleOrDefaultAsync(x => x.Id == draftId && x.OrganizationId == organizationId, ct);
    if (draft is null) return Results.NotFound();
    if (draft.Status is "Scheduled" or "Publishing" or "Published")
        return Results.Conflict(new { error = "برای ویرایش، ابتدا زمان‌بندی را لغو کنید؛ محتوای منتشرشده یا در حال انتشار قابل تغییر نیست." });
    if (draft.Status is "InReview" or "Approved" or "PublishFailed")
    {
        draft.Status = "Draft";
        draft.ApprovalRound++;
        draft.ApprovedAt = null;
        draft.ApprovedByUserId = null;
    }
    var title = request.Title?.Trim(); var content = request.Content?.Trim();
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || string.IsNullOrWhiteSpace(content) || content.Length > 30000)
        return Results.BadRequest(new { error = "عنوان و متن محتوا الزامی است و طول آن باید معتبر باشد." });
    draft.Title = title; draft.Content = content; draft.UpdatedAt = DateTimeOffset.UtcNow;
    if (draft.Status == "Approved") { draft.Status = "Draft"; draft.ApprovedAt = null; draft.ApprovedByUserId = null; }
    draft.Versions.Add(new ContentDraftVersion
    {
        DraftId = draft.Id, VersionNumber = draft.Versions.Count == 0 ? 1 : draft.Versions.Max(x => x.VersionNumber) + 1,
        Title = title, Content = content, ChangeNote = string.IsNullOrWhiteSpace(request.ChangeNote) ? "ویرایش محتوا" : request.ChangeNote.Trim()[..Math.Min(request.ChangeNote.Trim().Length, 300)],
        CreatedByUserId = userId.Value
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { draft.Id, draft.Title, draft.Content, draft.Status, draft.UpdatedAt, versionCount = draft.Versions.Count });
});

contentApi.MapGet("/drafts/{draftId:guid}/approvals", async (Guid organizationId, Guid draftId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var records = await db.ContentApprovalRecords.AsNoTracking()
        .Where(x => x.DraftId == draftId && x.OrganizationId == organizationId)
        .OrderBy(x => x.ReviewRound).ThenBy(x => x.StageNumber)
        .Select(x => new { x.Id, x.ReviewRound, x.StageNumber, x.Decision, x.Comment, x.ActorUserId, x.CreatedAt })
        .ToListAsync(ct);
    return Results.Ok(records);
});

contentApi.MapPost("/drafts/{draftId:guid}/approve", async (Guid organizationId, Guid draftId,
    ContentApprovalRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == draftId && x.OrganizationId == organizationId, ct);
    if (draft is null) return Results.NotFound();
    if (draft.Status is "Scheduled" or "Publishing" or "Published")
        return Results.Conflict(new { error = "محتوای زمان‌بندی‌شده یا منتشرشده قابل تأیید مجدد نیست." });
    if (string.IsNullOrWhiteSpace(draft.Content))
        return Results.Conflict(new { error = "محتوای خالی قابل تأیید نیست." });

    var round = Math.Max(1, draft.ApprovalRound);
    var existing = await db.ContentApprovalRecords.AsNoTracking()
        .Where(x => x.DraftId == draft.Id && x.ReviewRound == round)
        .OrderBy(x => x.StageNumber).ToListAsync(ct);
    if (existing.Any(x => x.ActorUserId == userId.Value))
        return Results.Conflict(new { error = "برای تکمیل تأیید دوم، مدیر دیگری باید بازبینی کند." });
    if (existing.Count >= 2)
        return Results.Conflict(new { error = "این دور بازبینی قبلاً تکمیل شده است." });

    var stage = existing.Count + 1;
    if (stage == 2 && draft.Status != "InReview")
        return Results.Conflict(new { error = "وضعیت درخواست بازبینی معتبر نیست." });

    db.ContentApprovalRecords.Add(new ContentApprovalRecord
    {
        DraftId = draft.Id, OrganizationId = organizationId, ReviewRound = round,
        StageNumber = stage, Decision = "Approved",
        Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim()[..Math.Min(request.Comment.Trim().Length, 1000)],
        ActorUserId = userId.Value, CreatedAt = DateTimeOffset.UtcNow
    });
    draft.Status = stage == 1 ? "InReview" : "Approved";
    draft.UpdatedAt = DateTimeOffset.UtcNow;
    if (stage == 2) { draft.ApprovedAt = DateTimeOffset.UtcNow; draft.ApprovedByUserId = userId.Value; }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new
    {
        draft.Id, draft.Status, draft.ApprovalRound, completedStage = stage,
        requiredStages = 2, draft.ApprovedAt,
        message = stage == 1 ? "مرحله اول ثبت شد؛ تأیید مدیر دوم لازم است." : "هر دو مرحله تأیید شد؛ محتوا آماده انتشار است."
    });
});

contentApi.MapPost("/drafts/{draftId:guid}/reject", async (Guid organizationId, Guid draftId,
    ContentApprovalRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == draftId && x.OrganizationId == organizationId, ct);
    if (draft is null) return Results.NotFound();
    if (draft.Status is "Scheduled" or "Publishing" or "Published")
        return Results.Conflict(new { error = "محتوای زمان‌بندی‌شده یا منتشرشده قابل رد نیست." });
    var round = Math.Max(1, draft.ApprovalRound);
    var existing = await db.ContentApprovalRecords.AsNoTracking()
        .Where(x => x.DraftId == draft.Id && x.ReviewRound == round)
        .ToListAsync(ct);
    if (existing.Any(x => x.ActorUserId == userId.Value))
        return Results.Conflict(new { error = "شما قبلاً در این دور بازبینی تصمیم ثبت کرده‌اید." });
    var comment = request.Comment?.Trim();
    if (string.IsNullOrWhiteSpace(comment))
        return Results.BadRequest(new { error = "برای رد محتوا، دلیل بازبینی الزامی است." });
    db.ContentApprovalRecords.Add(new ContentApprovalRecord
    {
        DraftId = draft.Id, OrganizationId = organizationId, ReviewRound = round,
        StageNumber = existing.Count + 1, Decision = "Rejected",
        Comment = comment[..Math.Min(comment.Length, 1000)],
        ActorUserId = userId.Value, CreatedAt = DateTimeOffset.UtcNow
    });
    draft.Status = "Draft"; draft.ApprovalRound++; draft.ApprovedAt = null; draft.ApprovedByUserId = null;
    draft.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { draft.Id, draft.Status, draft.ApprovalRound, message = "محتوا برای اصلاح به پیش‌نویس بازگشت." });
});

var inboxApi = app.MapGroup("/api/organizations/{organizationId:guid}/inbox").RequireAuthorization();

inboxApi.MapGet("/conversations", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    string? status, string? q, int? page, int? pageSize, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var query = db.InboxConversations.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(status) && status != "All")
        query = query.Where(x => x.Status == status);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        query = query.Where(x => (x.Subject != null && x.Subject.Contains(term)) ||
            (x.Contact != null && x.Contact.FullName.Contains(term)) ||
            (x.Messages.OrderByDescending(m => m.CreatedAt).Select(m => m.Body).FirstOrDefault() ?? "").Contains(term));
    }
    var total = await query.CountAsync(ct);
    var currentPage = Math.Clamp(page ?? 1, 1, 10000);
    var take = Math.Clamp(pageSize ?? 30, 1, 100);
    var items = await query.OrderByDescending(x => x.LastMessageAt)
        .Skip((currentPage - 1) * take).Take(take)
        .Select(x => new
        {
            x.Id, x.ChannelConnectionId, x.ContactId, x.ExternalConversationId, x.Subject,
            x.Status, x.AssignedToUserId, x.LastMessageAt, x.CreatedAt, x.UpdatedAt,
            Channel = x.ChannelConnection == null ? null : x.ChannelConnection.Provider,
            ChannelName = x.ChannelConnection == null ? null : x.ChannelConnection.DisplayName,
            ContactName = x.Contact == null ? null : x.Contact.FullName,
            MessageCount = x.Messages.Count,
            LastMessage = x.Messages.OrderByDescending(m => m.CreatedAt).Select(m => m.Body).FirstOrDefault(),
            LastDirection = x.Messages.OrderByDescending(m => m.CreatedAt).Select(m => m.Direction).FirstOrDefault()
        }).ToListAsync(ct);
    return Results.Ok(new { items, total, page = currentPage, pageSize = take });
});

inboxApi.MapGet("/conversations/{conversationId:guid}/messages", async (Guid organizationId, Guid conversationId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var exists = await db.InboxConversations.AnyAsync(x => x.Id == conversationId && x.OrganizationId == organizationId, ct);
    if (!exists) return Results.NotFound();
    var messages = await db.InboxMessages.AsNoTracking().Where(x => x.ConversationId == conversationId)
        .OrderBy(x => x.CreatedAt)
        .Select(x => new { x.Id, x.ExternalMessageId, x.Direction, x.Body, x.Status, x.SentAt, x.CreatedAt, x.CreatedByUserId })
        .Take(500).ToListAsync(ct);
    return Results.Ok(messages);
});

inboxApi.MapPost("/conversations", async (Guid organizationId, CreateInboxConversationRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    if (request.ChannelConnectionId is Guid channelId &&
        !await db.SocialChannelConnections.AnyAsync(x => x.Id == channelId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "کانال انتخاب‌شده متعلق به این سازمان نیست." });
    if (request.ContactId is Guid contactId &&
        !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    var subject = Clean(request.Subject);
    var externalId = Clean(request.ExternalConversationId);
    if ((subject?.Length ?? 0) > 240 || (externalId?.Length ?? 0) > 180)
        return Results.BadRequest(new { error = "طول عنوان یا شناسه مکالمه معتبر نیست." });
    var item = new InboxConversation
    {
        OrganizationId = organizationId, ChannelConnectionId = request.ChannelConnectionId,
        ContactId = request.ContactId, Subject = subject, ExternalConversationId = externalId,
        Status = "Open", LastMessageAt = DateTimeOffset.UtcNow
    };
    db.InboxConversations.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/inbox/conversations/{item.Id}",
        new { item.Id, item.Subject, item.Status, item.ChannelConnectionId, item.ContactId, item.CreatedAt });
});

inboxApi.MapPost("/conversations/{conversationId:guid}/messages", async (Guid organizationId, Guid conversationId,
    CreateInboxMessageRequest request, ClaimsPrincipal principal, MajaziDbContext db,
    IDataProtectionProvider protection, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var conversation = await db.InboxConversations
        .Include(x => x.ChannelConnection)
        .SingleOrDefaultAsync(x => x.Id == conversationId && x.OrganizationId == organizationId, ct);
    if (conversation is null) return Results.NotFound();
    var body = request.Body?.Trim();
    if (string.IsNullOrWhiteSpace(body) || body.Length > 10000)
        return Results.BadRequest(new { error = "متن پیام الزامی است و حداکثر 10000 کاراکتر می‌تواند باشد." });
    if (conversation.Status == "Resolved")
        return Results.Conflict(new { error = "مکالمه بسته شده است؛ ابتدا وضعیت آن را به باز تغییر دهید." });

    var message = new InboxMessage
    {
        ConversationId = conversationId, Direction = "Outbound", Body = body,
        Status = "Queued", CreatedByUserId = userId.Value
    };

    // Telegram replies are delivered synchronously through the official Bot API.
    // Other providers remain explicitly queued until their official delivery adapter is implemented.
    if (conversation.ChannelConnection?.Provider == "Telegram")
    {
        if (conversation.ChannelConnection.Status != "Connected" ||
            string.IsNullOrWhiteSpace(conversation.ChannelConnection.EncryptedAccessToken))
            return Results.Conflict(new { error = "ربات تلگرام متصل و تأییدشده نیست." });
        if (!long.TryParse(conversation.ExternalConversationId, out var chatId))
            return Results.Conflict(new { error = "شناسه گفتگوی تلگرام معتبر نیست؛ مکالمه باید از webhook تلگرام دریافت شده باشد." });

        string botToken;
        try
        {
            botToken = protection.CreateProtector("Majazi.SocialConnections.v1")
                .Unprotect(conversation.ChannelConnection.EncryptedAccessToken);
        }
        catch (CryptographicException)
        {
            return Results.Conflict(new { error = "توکن ربات قابل رمزگشایی نیست؛ اتصال تلگرام را دوباره برقرار کنید." });
        }

        try
        {
            using var response = await httpClientFactory.CreateClient("TelegramBotApi").PostAsJsonAsync(
                $"https://api.telegram.org/bot{botToken}/sendMessage",
                new { chat_id = chatId, text = body, disable_web_page_preview = true }, ct);
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = payload.RootElement;
            if (!response.IsSuccessStatusCode || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return Results.Problem("تلگرام پیام را نپذیرفت؛ پیام در سامانه ثبت نشد.", statusCode: 502);
            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("message_id", out var externalMessageId))
                message.ExternalMessageId = externalMessageId.ToString();
            message.Status = "Sent";
            message.SentAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem("ارسال پیام به تلگرام در زمان مقرر پاسخ نداد؛ وضعیت تحویل را در تلگرام بررسی کنید.", statusCode: 504);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("ارتباط با Telegram Bot API برقرار نشد؛ پیام در سامانه ثبت نشد.", statusCode: 502);
        }
    }

    db.InboxMessages.Add(message);
    conversation.LastMessageAt = message.CreatedAt;
    conversation.UpdatedAt = DateTimeOffset.UtcNow;
    conversation.Status = "Pending";
    await db.SaveChangesAsync(ct);
    var delivered = message.Status == "Sent";
    return Results.Created($"/api/organizations/{organizationId}/inbox/conversations/{conversationId}/messages/{message.Id}",
        new { message.Id, message.Direction, message.Body, message.Status, message.ExternalMessageId,
            message.SentAt, message.CreatedAt,
            deliveryNote = delivered
                ? "پیام با موفقیت از طریق Telegram Bot API ارسال شد."
                : "پیام فقط در صف داخلی ثبت شد؛ ارسال‌گر رسمی این کانال هنوز فعال نیست." });
});

inboxApi.MapPatch("/conversations/{conversationId:guid}", async (Guid organizationId, Guid conversationId,
    UpdateInboxConversationRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var item = await db.InboxConversations.SingleOrDefaultAsync(
        x => x.Id == conversationId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    if (request.Status is not null)
    {
        if (request.Status is not ("Open" or "Pending" or "Resolved"))
            return Results.BadRequest(new { error = "وضعیت مکالمه معتبر نیست." });
        item.Status = request.Status;
    }
    if (request.AssignedToUserId.HasValue)
    {
        var assignee = request.AssignedToUserId.Value;
        if (assignee != Guid.Empty && !await IsMember(db, assignee, organizationId, ct))
            return Results.BadRequest(new { error = "مسئول انتخاب‌شده عضو این سازمان نیست." });
        item.AssignedToUserId = assignee == Guid.Empty ? null : assignee;
    }
    item.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Status, item.AssignedToUserId, item.UpdatedAt });
});

app.Run();


static SocketsHttpHandler CreateEgressHandler(string? proxyAddress)
{
    var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    if (string.IsNullOrWhiteSpace(proxyAddress)) return handler;
    if (!Uri.TryCreate(proxyAddress, UriKind.Absolute, out var proxyUri) ||
        proxyUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(proxyUri.UserInfo))
        throw new InvalidOperationException("External egress proxy must be an HTTP(S) URL without embedded credentials.");
    handler.Proxy = new WebProxy(proxyUri);
    handler.UseProxy = true;
    return handler;
}

static Guid? CurrentUserId(ClaimsPrincipal principal)
{
    var value = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
    return Guid.TryParse(value, out var id) ? id : null;
}

static Task<bool> IsMember(MajaziDbContext db, Guid userId, Guid organizationId, CancellationToken ct) =>
    db.OrganizationMemberships.AnyAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);

static object IssueToken(AppUser user, IConfiguration config)
{
    var issuer = config["Jwt:Issuer"] ?? "majazi-api";
    var audience = config["Jwt:Audience"] ?? "majazi-web";
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var expires = DateTime.UtcNow.AddMinutes(60);
    var claims = new[] {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Email, user.Email),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };
    var token = new JwtSecurityToken(issuer, audience, claims, expires: expires, signingCredentials: credentials);
    return new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), tokenType = "Bearer", expiresAt = expires };
}

static string TelegramWebhookSignature(string signingKey, Guid connectionId) =>
    Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey),
        Encoding.UTF8.GetBytes("majazi:telegram:path:" + connectionId.ToString("D")))).ToLowerInvariant();

static bool IsValidTelegramChatTarget(string? target)
{
    if (string.IsNullOrWhiteSpace(target)) return false;
    var value = target.Trim();
    if (value.Length > 180) return false;
    if (long.TryParse(value, out _)) return true;
    return value.Length > 1 && value[0] == '@' && value[1..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
}

static string TelegramWebhookSecret(string signingKey, Guid connectionId) =>
    Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey),
        Encoding.UTF8.GetBytes("majazi:telegram:secret:" + connectionId.ToString("D")))).ToLowerInvariant();

static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

public sealed record RegisterRequest(string? Email, string? Password, string? OrganizationName, string? CaptchaId, string? CaptchaAnswer);
public sealed record LoginRequest(string? Email, string? Password, string? CaptchaId, string? CaptchaAnswer);
public sealed record CreateOrganizationRequest(string? Name, string? Slug);
public sealed record CreateBrandRequest(string? Name, string? Slug);
public sealed record AddMemberRequest(string? Email, string? Role);
public sealed record CrmContactRequest(Guid? BrandId, string? FullName, string? Email, string? Phone, string? Company, string? Notes);
public sealed record CrmLeadRequest(Guid? BrandId, Guid? ContactId, string? Title, string? Stage, decimal? EstimatedValue, string? Source, string? Notes);
public sealed record UpdateLeadStageRequest(string? Stage);
public sealed record CrmActivityRequest(Guid? ContactId, Guid? LeadId, string? ActivityType, string? Summary, DateTimeOffset? OccurredAt);

public sealed record CampaignRequest(Guid? BrandId, string? Name, string? Objective, string? Channel, string? Status, decimal Budget, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Notes, decimal ActualSpend = 0, decimal AttributedRevenue = 0, long Impressions = 0, long Clicks = 0, long Conversions = 0);

public sealed record SocialChannelRequest(Guid? BrandId, string? Provider, string? DisplayName, string? ExternalAccountId, string? AccessToken);

public sealed record CreateInboxConversationRequest(Guid? ChannelConnectionId, Guid? ContactId, string? ExternalConversationId, string? Subject);
public sealed record CreateInboxMessageRequest(string? Body);
public sealed record UpdateInboxConversationRequest(string? Status, Guid? AssignedToUserId);


record MetaOAuthState(Guid UserId, Guid OrganizationId, Guid? BrandId, string DisplayName);

public sealed record GenerateContentRequest(Guid? BrandId, string? Topic, string? Channel, string? Tone, string? Instructions);
public sealed record EditContentDraftRequest(string? Title, string? Content, string? ChangeNote);

public sealed record ScheduleContentRequest(Guid ConnectionId, string? TargetChatId, DateTimeOffset? ScheduledAt);
public sealed record PublishNowRequest(Guid ConnectionId, string? TargetChatId);


public sealed record ContentApprovalRequest(string? Comment);

public sealed record GrowthAgentSettingsRequest(bool Enabled);
