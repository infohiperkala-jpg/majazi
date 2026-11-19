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
builder.Services.AddHostedService<AutomationWorker>();
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

organizationsApi.MapPost("/demo/digibail", async (ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    try
    {
        var result = await DemoDataSeeder.SeedAsync(db, userId.Value, ct);
        return Results.Ok(result);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

organizationsApi.MapGet("/", async (ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var items = await db.OrganizationMemberships.AsNoTracking().Where(x => x.UserId == userId)
        .OrderByDescending(x => x.Organization!.Slug == "digibail-demo")
        .ThenBy(x => x.Organization!.Name)
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
    RecordAuditEvent(db, id, userId.Value, "organization.updated", "Organization", id, "مشخصات سازمان به‌روزرسانی شد.", new { item.Name, item.Slug });
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
    RecordAuditEvent(db, id, userId.Value, "member.added", "Membership", target.Id, $"کاربر {target.Email} با نقش {role} به سازمان افزوده شد.");
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{id}/members", new { userId = target.Id, email = target.Email, role });
});

organizationsApi.MapPut("/{id:guid}/members/{memberId:guid}", async (Guid id, Guid memberId, UpdateMemberRoleRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var actor = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (actor is null) return Results.NotFound();
    if (actor.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var target = await db.OrganizationMemberships.Include(x => x.User).SingleOrDefaultAsync(x => x.UserId == memberId && x.OrganizationId == id, ct);
    if (target is null) return Results.NotFound();
    var role = request.Role?.Trim();
    if (role is not ("Admin" or "Member")) return Results.BadRequest(new { error = "نقش باید مدیر یا عضو باشد." });
    if (target.Role == "Owner" || (actor.Role != "Owner" && target.Role == "Admin")) return Results.Forbid();
    if (memberId == userId.Value && role != actor.Role) return Results.BadRequest(new { error = "امکان تغییر نقش خودتان وجود ندارد." });
    target.Role = role;
    RecordAuditEvent(db, id, userId.Value, "member.role_changed", "Membership", memberId, $"نقش {target.User!.Email} به {role} تغییر کرد.");
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { userId = target.UserId, email = target.User!.Email, target.Role, target.CreatedAt });
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
    RecordAuditEvent(db, id, userId.Value, "member.removed", "Membership", memberId, $"دسترسی عضو {memberId} لغو شد.");
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

organizationsApi.MapGet("/{id:guid}/audit-events", async (
    Guid id, int? page, int? pageSize, string? action, string? q,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.AsNoTracking()
        .SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == id, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();

    var currentPage = Math.Clamp(page ?? 1, 1, 100000);
    var size = Math.Clamp(pageSize ?? 30, 10, 100);
    var query = db.OrganizationAuditEvents.AsNoTracking().Where(x => x.OrganizationId == id);
    if (!string.IsNullOrWhiteSpace(action))
    {
        var actionFilter = action.Trim();
        query = query.Where(x => x.Action == actionFilter);
    }
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        if (term.Length > 120) return Results.BadRequest(new { error = "عبارت جست‌وجو بیش از حد طولانی است." });
        query = query.Where(x => x.Summary.Contains(term) || x.EntityType.Contains(term) || x.Action.Contains(term));
    }
    var total = await query.CountAsync(ct);
    var items = await query.OrderByDescending(x => x.CreatedAt)
        .Skip((currentPage - 1) * size).Take(size)
        .Select(e => new
        {
            e.Id, e.Action, e.EntityType, e.EntityId, e.Summary, e.MetadataJson, e.CreatedAt,
            ActorEmail = db.Users.AsNoTracking().Where(u => e.ActorUserId == u.Id).Select(u => u.Email).FirstOrDefault()
        })
        .ToListAsync(ct);
    var actions = await db.OrganizationAuditEvents.AsNoTracking().Where(x => x.OrganizationId == id)
        .Select(x => x.Action).Distinct().OrderBy(x => x).ToListAsync(ct);
    return Results.Ok(new { items, total, page = currentPage, pageSize = size, actions });
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


const string statusKeyPattern = "^[a-z0-9][a-z0-9._-]{0,63}$";
var statusesApi = app.MapGroup("/api/organizations/{organizationId:guid}/statuses").RequireAuthorization();
var supportedStatusModules = new HashSet<string>(["crm.lead", "campaign", "inbox.conversation"]);
var defaultStatusesByModule = new Dictionary<string, (string Key, string Label, string Color)[]>
{
    ["crm.lead"] = [("new", "جدید", "#64748b"), ("qualified", "ارزیابی‌شده", "#2563eb"), ("proposal", "پیشنهاد", "#7c3aed"), ("negotiation", "مذاکره", "#d97706"), ("won", "موفق", "#16a34a"), ("lost", "از دست‌رفته", "#dc2626")],
    ["campaign"] = [("draft", "پیش‌نویس", "#64748b"), ("scheduled", "زمان‌بندی‌شده", "#2563eb"), ("active", "فعال", "#16a34a"), ("paused", "متوقف", "#d97706"), ("completed", "پایان‌یافته", "#0f766e"), ("cancelled", "لغوشده", "#dc2626")],
    ["inbox.conversation"] = [("open", "باز", "#2563eb"), ("pending", "در انتظار پاسخ", "#d97706"), ("resolved", "بسته‌شده", "#16a34a")],
};

statusesApi.MapGet("/", async (Guid organizationId, string? module, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    IEnumerable<string> modulesToSeed = string.IsNullOrWhiteSpace(module)
        ? defaultStatusesByModule.Keys
        : defaultStatusesByModule.ContainsKey(module) ? new[] { module } : Array.Empty<string>();
    foreach (var targetModule in modulesToSeed)
    {
        if (await db.StatusDefinitions.AnyAsync(x => x.OrganizationId == organizationId && x.Module == targetModule, ct)) continue;
        var now = DateTimeOffset.UtcNow;
        db.StatusDefinitions.AddRange(defaultStatusesByModule[targetModule].Select((item, index) => new StatusDefinition
        {
            OrganizationId = organizationId, Module = targetModule, Key = item.Key, Label = item.Label,
            Color = item.Color, SortOrder = (index + 1) * 10, IsActive = true, CreatedAt = now, UpdatedAt = now
        }));
    }
    if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    var query = db.StatusDefinitions.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(module)) query = query.Where(x => x.Module == module);
    var items = await query.OrderBy(x => x.Module).ThenBy(x => x.SortOrder).ThenBy(x => x.Label)
        .Select(x => new { x.Id, x.Module, x.Key, x.Label, x.Color, x.Description, x.SortOrder, x.IsActive, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(items);
});

statusesApi.MapPost("/", async (Guid organizationId, StatusDefinitionRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var module = request.Module?.Trim().ToLowerInvariant(); var key = request.Key?.Trim().ToLowerInvariant();
    var label = request.Label?.Trim(); var color = request.Color?.Trim();
    if (module is null || !supportedStatusModules.Contains(module) || key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, statusKeyPattern) ||
        string.IsNullOrWhiteSpace(label) || label.Length > 100 || color is null || !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ||
        (request.Description?.Length ?? 0) > 500 || request.SortOrder < 0 || request.SortOrder > 10000)
        return Results.BadRequest(new { error = "ماژول، کلید، عنوان، رنگ یا ترتیب وضعیت معتبر نیست." });
    if (await db.StatusDefinitions.AnyAsync(x => x.OrganizationId == organizationId && x.Module == module && x.Key == key, ct))
        return Results.Conflict(new { error = "این کلید وضعیت در این ماژول قبلاً ثبت شده است." });
    var item = new StatusDefinition { OrganizationId = organizationId, Module = module, Key = key, Label = label, Color = color,
        Description = Clean(request.Description), SortOrder = request.SortOrder, IsActive = request.IsActive };
    db.StatusDefinitions.Add(item); await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/statuses/{item.Id}", new { item.Id, item.Module, item.Key, item.Label, item.Color, item.Description, item.SortOrder, item.IsActive, item.CreatedAt, item.UpdatedAt });
});

statusesApi.MapPut("/{statusId:guid}", async (Guid organizationId, Guid statusId, UpdateStatusDefinitionRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.StatusDefinitions.SingleOrDefaultAsync(x => x.Id == statusId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    var label = request.Label?.Trim(); var color = request.Color?.Trim();
    if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || color is null || !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$") ||
        (request.Description?.Length ?? 0) > 500 || request.SortOrder < 0 || request.SortOrder > 10000)
        return Results.BadRequest(new { error = "عنوان، رنگ، توضیح یا ترتیب وضعیت معتبر نیست." });
    item.Label = label; item.Color = color; item.Description = Clean(request.Description);
    item.SortOrder = request.SortOrder; item.IsActive = request.IsActive; item.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Module, item.Key, item.Label, item.Color, item.Description, item.SortOrder, item.IsActive, item.UpdatedAt });
});

statusesApi.MapDelete("/{statusId:guid}", async (Guid organizationId, Guid statusId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.StatusDefinitions.SingleOrDefaultAsync(x => x.Id == statusId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    item.IsActive = false; item.UpdatedAt = DateTimeOffset.UtcNow;
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
    RecordAuditEvent(db, organizationId, userId.Value, "crm.contact_created", "CrmContact", item.Id, "مخاطب جدید ایجاد شد.");
    await QueueAutomationRunsAsync(db, organizationId, "ContactCreated", "Contact", item.Id, new { contactName = item.FullName, company = item.Company }, ct);
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
    RecordAuditEvent(db, organizationId, userId.Value, "crm.contact_updated", "CrmContact", item.Id, "اطلاعات مخاطب ویرایش شد.");
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
    RecordAuditEvent(db, organizationId, userId.Value, "crm.contact_deleted", "CrmContact", contactId, "مخاطب حذف شد.");
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
            ContactName = db.CrmContacts.Where(c => c.Id == x.ContactId && c.OrganizationId == organizationId).Select(c => c.FullName).FirstOrDefault() }).ToListAsync(ct);
    return Results.Ok(items);
});

crmApi.MapPost("/leads", async (Guid organizationId, CrmLeadRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var title = request.Title?.Trim();
    var stage = request.Stage?.Trim() ?? "new";
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || !await IsAllowedConfiguredStatus(db, organizationId, "crm.lead", stage, leadStages, ct) ||
        request.EstimatedValue < 0 || (request.Source?.Length ?? 0) > 120 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "عنوان، مرحله یا مبلغ فرصت فروش معتبر نیست." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    if (request.ContactId is Guid contactId && !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    var item = new CrmLead { OrganizationId = organizationId, BrandId = request.BrandId, ContactId = request.ContactId,
        Title = title, Stage = stage, EstimatedValue = request.EstimatedValue, Source = Clean(request.Source), Notes = Clean(request.Notes) };
    db.CrmLeads.Add(item);
    RecordAuditEvent(db, organizationId, userId.Value, "crm.lead_created", "CrmLead", item.Id, "فرصت فروش ایجاد شد.", new { item.Stage });
    await QueueAutomationRunsAsync(db, organizationId, "LeadCreated", "Lead", item.Id, new { leadTitle = item.Title, leadStage = item.Stage, leadSource = item.Source }, ct);
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
    var previousStage = item.Stage;
    item.Title = title; item.Stage = stage; item.BrandId = request.BrandId; item.ContactId = request.ContactId;
    item.EstimatedValue = request.EstimatedValue; item.Source = Clean(request.Source); item.Notes = Clean(request.Notes);
    item.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "crm.lead_updated", "CrmLead", item.Id, "اطلاعات فرصت فروش ویرایش شد.", new { previousStage, newStage = item.Stage });
    if (!string.Equals(previousStage, item.Stage, StringComparison.OrdinalIgnoreCase))
        await QueueAutomationRunsAsync(db, organizationId, "LeadStageChanged", "Lead", item.Id, new { previousStage, newStage = item.Stage }, ct);
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
    RecordAuditEvent(db, organizationId, userId.Value, "crm.lead_deleted", "CrmLead", leadId, "فرصت فروش حذف شد.");
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

crmApi.MapPatch("/leads/{leadId:guid}/stage", async (Guid organizationId, Guid leadId, UpdateLeadStageRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    if (request.Stage is null || !await IsAllowedConfiguredStatus(db, organizationId, "crm.lead", request.Stage, leadStages, ct)) return Results.BadRequest(new { error = "مرحله فروش معتبر نیست." });
    var item = await db.CrmLeads.SingleOrDefaultAsync(x => x.Id == leadId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    var previousStage = item.Stage;
    item.Stage = request.Stage;
    item.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "crm.lead_stage_changed", "CrmLead", item.Id, "مرحله فرصت فروش تغییر کرد.", new { previousStage, newStage = item.Stage });
    if (!string.Equals(previousStage, item.Stage, StringComparison.OrdinalIgnoreCase))
        await QueueAutomationRunsAsync(db, organizationId, "LeadStageChanged", "Lead", item.Id, new { previousStage, newStage = item.Stage }, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Stage, item.UpdatedAt });
});

crmApi.MapGet("/activities", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var items = await db.CrmActivities.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .OrderByDescending(x => x.OccurredAt).Take(100)
        .Select(x => new { x.Id, x.ContactId, x.LeadId, x.ActivityType, x.Summary, x.OccurredAt, x.CreatedByUserId, ContactName = db.CrmContacts.Where(c => c.Id == x.ContactId).Select(c => c.FullName).FirstOrDefault(), LeadTitle = x.Lead == null ? null : x.Lead.Title }).ToListAsync(ct);
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
    RecordAuditEvent(db, organizationId, userId.Value, "crm.activity_created", "CrmActivity", item.Id, "فعالیت CRM ثبت شد.", new { item.ActivityType });
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/crm/activities/{item.Id}", new { item.Id, item.ContactId, item.LeadId, item.ActivityType, item.Summary, item.OccurredAt, item.CreatedByUserId });
});


var ordersApi = app.MapGroup("/api/organizations/{organizationId:guid}/orders").RequireAuthorization();
var orderStatuses = new HashSet<string>(["Draft", "Confirmed", "Processing", "Completed", "Cancelled"]);
var paymentMethods = new HashSet<string>(["Cash", "BankTransfer", "Card", "Gateway", "Other"]);
var paymentStatuses = new HashSet<string>(["Pending", "Paid", "Failed", "Refunded"]);

ordersApi.MapGet("/", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    string? status, string? q, string? assignment, int? page, int? pageSize, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var currentPage = Math.Clamp(page ?? 1, 1, 100000);
    var size = Math.Clamp(pageSize ?? 30, 10, 100);
    var query = db.SalesOrders.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(status) && status != "All") query = query.Where(x => x.Status == status);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        if (term.Length > 120) return Results.BadRequest(new { error = "عبارت جست‌وجو بیش از حد طولانی است." });
        query = query.Where(x => x.OrderNumber.Contains(term) || (x.Notes != null && x.Notes.Contains(term)) ||
            db.CrmContacts.Any(c => c.Id == x.ContactId && c.OrganizationId == organizationId &&
                (c.FullName.Contains(term) || (c.Email != null && c.Email.Contains(term)))));
    }
    var total = await query.CountAsync(ct);
    var items = await query.OrderByDescending(x => x.CreatedAt).Skip((currentPage - 1) * size).Take(size)
        .Select(x => new
        {
            x.Id, x.OrderNumber, x.Status, x.Currency, x.Subtotal, x.Discount, x.Tax, x.Total, x.Version, x.CreatedAt, x.UpdatedAt,
            x.ContactId, ContactName = db.CrmContacts.Where(c => c.Id == x.ContactId).Select(c => c.FullName).FirstOrDefault(),
            LineCount = db.SalesOrderLines.Count(l => l.OrderId == x.Id),
            Paid = db.PaymentTransactions.Where(p => p.OrderId == x.Id && p.Status == "Paid").Sum(p => (decimal?)p.Amount) ?? 0,
            Refunded = db.PaymentTransactions.Where(p => p.OrderId == x.Id && p.Status == "Refunded").Sum(p => (decimal?)p.Amount) ?? 0
        }).ToListAsync(ct);
    return Results.Ok(new { items = items.Select(x => new { x.Id, x.OrderNumber, x.Status, x.Currency, x.Subtotal, x.Discount, x.Tax, x.Total, x.Version, x.CreatedAt, x.UpdatedAt, x.ContactId, x.ContactName, x.LineCount, x.Paid, x.Refunded, BalanceDue = Math.Max(0, x.Total - x.Paid + x.Refunded) }), total, page = currentPage, pageSize = size });
});

ordersApi.MapGet("/{orderId:guid}", async (Guid organizationId, Guid orderId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var order = await db.SalesOrders.AsNoTracking().Where(x => x.Id == orderId && x.OrganizationId == organizationId)
        .Select(x => new { x.Id, x.OrderNumber, x.ContactId, ContactName = db.CrmContacts.Where(c => c.Id == x.ContactId && c.OrganizationId == organizationId).Select(c => c.FullName).FirstOrDefault(),
            x.Status, x.Currency, x.Subtotal, x.Discount, x.Tax, x.Total, x.Notes, x.Version, x.CreatedByUserId, x.CreatedAt, x.UpdatedAt })
        .SingleOrDefaultAsync(ct);
    if (order is null) return Results.NotFound();
    var lines = await db.SalesOrderLines.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.SortOrder)
        .Select(x => new { x.Id, x.Name, x.Sku, x.Quantity, x.UnitPrice, x.Discount, x.LineTotal, x.SortOrder }).ToListAsync(ct);
    var payments = await db.PaymentTransactions.AsNoTracking().Where(x => x.OrderId == orderId).OrderByDescending(x => x.CreatedAt)
        .Select(x => new { x.Id, x.Amount, x.Method, x.Status, x.Reference, x.Note, x.PaidAt, x.CreatedAt }).ToListAsync(ct);
    var paid = payments.Where(x => x.Status == "Paid").Sum(x => x.Amount);
    var refunded = payments.Where(x => x.Status == "Refunded").Sum(x => x.Amount);
    return Results.Ok(new { order, lines, payments, paid, refunded, balanceDue = Math.Max(0, order.Total - paid + refunded) });
});

ordersApi.MapPost("/", async (Guid organizationId, SalesOrderRequest request, ClaimsPrincipal principal,
    MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var lines = request.Lines ?? [];
    var currency = (request.Currency ?? "IRR").Trim().ToUpperInvariant();
    if (lines.Count is < 1 or > 50 || currency.Length != 3 || !currency.All(char.IsAsciiLetter) ||
        request.Discount < 0 || request.Tax < 0 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "حداقل یک ردیف کالا، ارز سه‌حرفی و مقادیر مالی معتبر الزامی است." });
    if (request.ContactId is Guid contactId && !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    var calculated = new List<SalesOrderLine>();
    decimal subtotal = 0;
    foreach (var (line, index) in lines.Select((value, index) => (value, index)))
    {
        var name = line.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || (line.Sku?.Length ?? 0) > 100 ||
            line.Quantity <= 0 || line.Quantity > 1000000 || line.UnitPrice < 0 || line.UnitPrice > 9999999999999999m ||
            line.Discount < 0 || line.Discount > line.Quantity * line.UnitPrice)
            return Results.BadRequest(new { error = "نام، تعداد، قیمت یا تخفیف یکی از ردیف‌ها معتبر نیست." });
        var quantity = Math.Round(line.Quantity, 3);
        var gross = Math.Round(quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
        if (quantity <= 0 || gross > 9999999999999999m) return Results.BadRequest(new { error = "مبلغ یک ردیف از محدوده مجاز بیشتر است." });
        var lineDiscount = Math.Round(line.Discount, 2, MidpointRounding.AwayFromZero);
        if (lineDiscount > gross) return Results.BadRequest(new { error = "تخفیف ردیف از مبلغ ناخالص بیشتر است." });
        var lineTotal = gross - lineDiscount;
        subtotal += lineTotal;
        calculated.Add(new SalesOrderLine { Name = name, Sku = Clean(line.Sku), Quantity = quantity, UnitPrice = Math.Round(line.UnitPrice, 2), Discount = lineDiscount, LineTotal = lineTotal, SortOrder = index });
    }
    var discount = Math.Round(request.Discount, 2);
    var tax = Math.Round(request.Tax, 2);
    if (subtotal > 9999999999999999m || discount > subtotal || subtotal - discount + tax > 9999999999999999m)
        return Results.BadRequest(new { error = "مبلغ تخفیف یا مبلغ نهایی خارج از محدوده معتبر است." });
    var order = new SalesOrder
    {
        OrganizationId = organizationId, ContactId = request.ContactId,
        OrderNumber = "MZ-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        Currency = currency, Subtotal = subtotal, Discount = discount, Tax = tax, Total = subtotal - discount + tax,
        Notes = Clean(request.Notes), CreatedByUserId = userId.Value
    };
    db.SalesOrders.Add(order);
    foreach (var line in calculated) line.OrderId = order.Id;
    db.SalesOrderLines.AddRange(calculated);
    RecordAuditEvent(db, organizationId, userId.Value, "order.created", "SalesOrder", order.Id, "سفارش جدید ثبت شد.", new { order.OrderNumber, order.Total, order.Currency, lineCount = calculated.Count });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "شماره سفارش تکراری شد؛ دوباره تلاش کنید." }); }
    return Results.Created($"/api/organizations/{organizationId}/orders/{order.Id}", new { order.Id, order.OrderNumber, order.Status, order.Currency, order.Subtotal, order.Discount, order.Tax, order.Total, order.Version, order.CreatedAt });
});

ordersApi.MapPut("/{orderId:guid}", async (Guid organizationId, Guid orderId, SalesOrderRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var order = await db.SalesOrders.SingleOrDefaultAsync(x => x.Id == orderId && x.OrganizationId == organizationId, ct);
    if (order is null) return Results.NotFound();
    if (order.Status != "Draft") return Results.Conflict(new { error = "فقط سفارش پیش‌نویس قابل ویرایش است." });
    if (request.Version != order.Version) return Results.Conflict(new { error = "سفارش توسط کاربر دیگری تغییر کرده است؛ اطلاعات را تازه‌سازی کنید." });
    var lines = request.Lines ?? [];
    var currency = (request.Currency ?? order.Currency).Trim().ToUpperInvariant();
    if (lines.Count is < 1 or > 50 || currency.Length != 3 || !currency.All(char.IsAsciiLetter) ||
        request.Discount < 0 || request.Tax < 0 || (request.Notes?.Length ?? 0) > 4000)
        return Results.BadRequest(new { error = "ردیف‌ها یا مقادیر مالی معتبر نیستند." });
    if (request.ContactId is Guid contactId && !await db.CrmContacts.AnyAsync(x => x.Id == contactId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "مخاطب انتخاب‌شده متعلق به این سازمان نیست." });
    var calculated = new List<SalesOrderLine>(); decimal subtotal = 0;
    foreach (var (line, index) in lines.Select((value, index) => (value, index)))
    {
        var name = line.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || (line.Sku?.Length ?? 0) > 100 ||
            line.Quantity <= 0 || line.Quantity > 1000000 || line.UnitPrice < 0 || line.UnitPrice > 9999999999999999m ||
            line.Discount < 0 || line.Discount > line.Quantity * line.UnitPrice)
            return Results.BadRequest(new { error = "نام، تعداد، قیمت یا تخفیف یکی از ردیف‌ها معتبر نیست." });
        var quantity = Math.Round(line.Quantity, 3);
        var gross = Math.Round(quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
        if (gross > 9999999999999999m) return Results.BadRequest(new { error = "مبلغ یک ردیف از محدوده مجاز بیشتر است." });
        var lineDiscount = Math.Round(line.Discount, 2);
        if (quantity <= 0 || lineDiscount > gross) return Results.BadRequest(new { error = "تعداد یا تخفیف ردیف معتبر نیست." });
        var lineTotal = gross - lineDiscount; subtotal += lineTotal;
        calculated.Add(new SalesOrderLine { OrderId = order.Id, Name = name, Sku = Clean(line.Sku), Quantity = quantity, UnitPrice = Math.Round(line.UnitPrice, 2), Discount = lineDiscount, LineTotal = lineTotal, SortOrder = index });
    }
    var discount = Math.Round(request.Discount, 2); var tax = Math.Round(request.Tax, 2);
    if (subtotal > 9999999999999999m || discount > subtotal || subtotal - discount + tax > 9999999999999999m)
        return Results.BadRequest(new { error = "مبلغ تخفیف یا مبلغ نهایی معتبر نیست." });
    order.ContactId = request.ContactId; order.Currency = currency; order.Subtotal = subtotal; order.Discount = discount;
    order.Tax = tax; order.Total = subtotal - discount + tax; order.Notes = Clean(request.Notes);
    order.Version++; order.UpdatedAt = DateTimeOffset.UtcNow;
    var oldLines = await db.SalesOrderLines.Where(x => x.OrderId == orderId).ToListAsync(ct);
    db.SalesOrderLines.RemoveRange(oldLines); db.SalesOrderLines.AddRange(calculated);
    RecordAuditEvent(db, organizationId, userId.Value, "order.updated", "SalesOrder", order.Id, "پیش‌نویس سفارش ویرایش شد.", new { order.Version, order.Total });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "سفارش هم‌زمان تغییر کرده است؛ صفحه را تازه‌سازی کنید." }); }
    return Results.Ok(new { order.Id, order.OrderNumber, order.Status, order.Currency, order.Total, order.Version, order.UpdatedAt });
});

ordersApi.MapPatch("/{orderId:guid}/status", async (Guid organizationId, Guid orderId, SalesOrderStatusRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var order = await db.SalesOrders.SingleOrDefaultAsync(x => x.Id == orderId && x.OrganizationId == organizationId, ct);
    if (order is null) return Results.NotFound();
    var next = request.Status?.Trim();
    var allowed = order.Status switch
    {
        "Draft" => new[] { "Confirmed", "Cancelled" },
        "Confirmed" => new[] { "Processing", "Cancelled" },
        "Processing" => new[] { "Completed", "Cancelled" },
        _ => Array.Empty<string>()
    };
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId).Select(x => x.Role).SingleAsync(ct);
    if (next is null || !allowed.Contains(next) || (next == "Cancelled" && role is not ("Owner" or "Admin")))
        return Results.BadRequest(new { error = "انتقال وضعیت سفارش مجاز نیست." });
    var previous = order.Status; order.Status = next; order.Version++; order.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "order.status_changed", "SalesOrder", order.Id, "وضعیت سفارش تغییر کرد.", new { previous, next });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "سفارش هم‌زمان تغییر کرده است؛ اطلاعات را تازه‌سازی کنید." }); }
    return Results.Ok(new { order.Id, order.Status, order.Version, order.UpdatedAt });
});

ordersApi.MapPost("/{orderId:guid}/payments", async (Guid organizationId, Guid orderId, PaymentTransactionRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var order = await db.SalesOrders.SingleOrDefaultAsync(x => x.Id == orderId && x.OrganizationId == organizationId, ct);
    if (order is null) return Results.NotFound();
    var method = request.Method?.Trim(); var paymentStatus = request.Status?.Trim();
    if (order.Status is "Draft" or "Cancelled" || request.Amount <= 0 || request.Amount > 9999999999999999m ||
        method is null || !paymentMethods.Contains(method) || paymentStatus is null || !paymentStatuses.Contains(paymentStatus) ||
        (request.Reference?.Length ?? 0) > 180 || (request.Note?.Length ?? 0) > 1000)
        return Results.BadRequest(new { error = "سفارش یا اطلاعات پرداخت معتبر نیست." });
    var paid = await db.PaymentTransactions.Where(x => x.OrderId == orderId && x.Status == "Paid").SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
    var refunded = await db.PaymentTransactions.Where(x => x.OrderId == orderId && x.Status == "Refunded").SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
    if (paymentStatus == "Paid" && request.Amount > order.Total - paid + refunded)
        return Results.BadRequest(new { error = "مبلغ پرداخت از مانده قابل پرداخت بیشتر است." });
    if (paymentStatus == "Refunded" && request.Amount > paid - refunded)
        return Results.BadRequest(new { error = "مبلغ بازپرداخت از مبلغ پرداخت‌شده خالص بیشتر است." });
    var payment = new PaymentTransaction
    {
        OrderId = orderId, Amount = Math.Round(request.Amount, 2), Method = method, Status = paymentStatus,
        Reference = Clean(request.Reference), Note = Clean(request.Note), PaidAt = paymentStatus == "Paid" ? DateTimeOffset.UtcNow : null,
        CreatedByUserId = userId.Value
    };
    db.PaymentTransactions.Add(payment);
    order.Version++; order.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "order.payment_recorded", "PaymentTransaction", payment.Id, "رویداد مالی سفارش ثبت شد.", new { payment.Amount, payment.Method, payment.Status });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "سفارش هم‌زمان به‌روزرسانی شده است؛ مانده را تازه‌سازی کنید." }); }
    return Results.Created($"/api/organizations/{organizationId}/orders/{orderId}", new { payment.Id, payment.Amount, payment.Method, payment.Status, payment.PaidAt, payment.CreatedAt });
});

ordersApi.MapPatch("/{orderId:guid}/payments/{paymentId:guid}/status", async (Guid organizationId, Guid orderId, Guid paymentId,
    PaymentStatusUpdateRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var order = await db.SalesOrders.SingleOrDefaultAsync(x => x.Id == orderId && x.OrganizationId == organizationId, ct);
    if (order is null) return Results.NotFound();
    var payment = await db.PaymentTransactions.SingleOrDefaultAsync(x => x.Id == paymentId && x.OrderId == orderId, ct);
    if (payment is null) return Results.NotFound();
    var next = request.Status?.Trim();
    if (payment.Status != "Pending" || next is not ("Paid" or "Failed") || order.Status is "Draft" or "Cancelled")
        return Results.Conflict(new { error = "فقط پرداخت در انتظار را می‌توان به پرداخت‌شده یا ناموفق تغییر داد." });
    if (next == "Paid")
    {
        var paid = await db.PaymentTransactions.Where(x => x.OrderId == orderId && x.Status == "Paid")
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        var refunded = await db.PaymentTransactions.Where(x => x.OrderId == orderId && x.Status == "Refunded")
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0;
        if (payment.Amount > order.Total - paid + refunded)
            return Results.BadRequest(new { error = "با ثبت این پرداخت، مجموع پرداخت‌ها از مبلغ سفارش بیشتر می‌شود." });
        payment.PaidAt = DateTimeOffset.UtcNow;
    }
    payment.Status = next;
    order.Version++;
    order.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "order.payment_status_changed", "PaymentTransaction", payment.Id,
        "وضعیت رویداد پرداخت تغییر کرد.", new { previous = "Pending", next, payment.Amount });
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "سفارش هم‌زمان تغییر کرده است؛ اطلاعات را تازه‌سازی کنید." }); }
    return Results.Ok(new { payment.Id, payment.Amount, payment.Status, payment.PaidAt, order.Version });
});

ordersApi.MapDelete("/{orderId:guid}", async (Guid organizationId, Guid orderId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var order = await db.SalesOrders.SingleOrDefaultAsync(x => x.Id == orderId && x.OrganizationId == organizationId, ct);
    if (order is null) return Results.NotFound();
    if (order.Status != "Draft" || await db.PaymentTransactions.AnyAsync(x => x.OrderId == orderId, ct))
        return Results.Conflict(new { error = "فقط سفارش پیش‌نویس بدون سابقه پرداخت قابل حذف است." });
    db.SalesOrders.Remove(order);
    RecordAuditEvent(db, organizationId, userId.Value, "order.deleted", "SalesOrder", order.Id, "پیش‌نویس سفارش حذف شد.");
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
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
    var name = request.Name?.Trim(); var channel = request.Channel?.Trim(); var status = request.Status?.Trim() ?? "draft";
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || channel is null || !campaignChannels.Contains(channel) ||
        !await IsAllowedConfiguredStatus(db, organizationId, "campaign", status, campaignStatuses, ct) || request.Budget < 0 || request.Budget > 9999999999999999m ||
        (request.Objective?.Length ?? 0) > 1000 || (request.Notes?.Length ?? 0) > 4000 ||
        request.ActualSpend < 0 || request.AttributedRevenue < 0 || request.Impressions < 0 || request.Clicks < 0 || request.Conversions < 0 || request.Clicks > request.Impressions || request.Conversions > request.Clicks ||
        (request.StartsAt.HasValue && request.EndsAt.HasValue && request.EndsAt < request.StartsAt))
        return Results.BadRequest(new { error = "اطلاعات کمپین، بودجه، کانال یا بازه زمانی معتبر نیست." });
    if (request.BrandId is Guid brandId && !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    var item = new MarketingCampaign { OrganizationId = organizationId, BrandId = request.BrandId, Name = name,
        Objective = Clean(request.Objective), Channel = channel, Status = status, Budget = request.Budget,
        StartsAt = request.StartsAt, EndsAt = request.EndsAt, Notes = Clean(request.Notes), ActualSpend = request.ActualSpend, AttributedRevenue = request.AttributedRevenue, Impressions = request.Impressions, Clicks = request.Clicks, Conversions = request.Conversions };
    db.MarketingCampaigns.Add(item); RecordAuditEvent(db, organizationId, userId.Value, "campaign.created", "MarketingCampaign", item.Id, "کمپین ایجاد شد.", new { item.Channel, item.Status }); await db.SaveChangesAsync(ct);
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
    RecordAuditEvent(db, organizationId, userId.Value, "campaign.updated", "MarketingCampaign", item.Id, "اطلاعات کمپین ویرایش شد.", new { item.Channel, item.Status });
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
    db.MarketingCampaigns.Remove(item); RecordAuditEvent(db, organizationId, userId.Value, "campaign.deleted", "MarketingCampaign", campaignId, "کمپین حذف شد."); await db.SaveChangesAsync(ct); return Results.NoContent();
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
        ["scope"] = "instagram_business_basic,instagram_business_manage_messages,instagram_business_content_publish",
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


var channelProviders = new HashSet<string>(["Instagram", "Facebook", "Telegram", "WhatsApp", "Bale", "Rubika"]);
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
    RecordAuditEvent(db, organizationId, userId.Value, "channel.connected", "SocialChannelConnection", item.Id, "اتصال کانال اجتماعی ثبت شد.", new { item.Provider, item.Status });
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/channels/{item.Id}",
        new { item.Id, item.BrandId, item.Provider, item.DisplayName, item.ExternalAccountId, item.Status,
            HasCredential = item.EncryptedAccessToken != null, item.CreatedAt });
});
channelsApi.MapPost("/{connectionId:guid}/meta-webhook", async (Guid organizationId, Guid connectionId,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IDataProtectionProvider protection,
    IHttpClientFactory clients, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.SocialChannelConnections.SingleOrDefaultAsync(
        x => x.Id == connectionId && x.OrganizationId == organizationId && (x.Provider == "Instagram" || x.Provider == "Facebook"), ct);
    if (item is null) return Results.NotFound();
    if (item.Status != "Connected" || string.IsNullOrWhiteSpace(item.EncryptedAccessToken) ||
        string.IsNullOrWhiteSpace(item.ExternalAccountId))
        return Results.Conflict(new { error = "ابتدا حساب و توکن Meta را ثبت و با آزمون زنده تأیید کنید." });
    string token;
    try { token = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(item.EncryptedAccessToken); }
    catch (CryptographicException) { return Results.Conflict(new { error = "توکن Meta قابل رمزگشایی نیست؛ اتصال را دوباره برقرار کنید." }); }
    var version = config["Meta:GraphApiVersion"] ?? "v22.0";
    var host = item.Provider == "Instagram" ? "graph.instagram.com" : "graph.facebook.com";
    var uri = $"https://{host}/{version}/{Uri.EscapeDataString(item.ExternalAccountId)}/subscribed_apps";
    using var message = new HttpRequestMessage(HttpMethod.Post, uri);
    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    message.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["subscribed_fields"] = "messages" });
    try
    {
        using var response = await clients.CreateClient("MetaOAuth").SendAsync(message, ct);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!response.IsSuccessStatusCode || !payload.RootElement.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
            return Results.Problem("Meta اشتراک وب‌هوک پیام‌ها را نپذیرفت؛ مجوزهای messaging و دسترسی حساب را بررسی کنید.", statusCode: 502);
        return Results.Ok(new { configured = true, provider = item.Provider, callbackUrl = (config["PublicBaseUrl"] ?? "").TrimEnd('/') + "/api/webhooks/meta" });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    { return Results.Problem("ثبت وب‌هوک Meta در زمان مقرر پاسخ نداد.", statusCode: 504); }
    catch (HttpRequestException)
    { return Results.Problem("ارتباط با Meta برای ثبت وب‌هوک برقرار نشد.", statusCode: 502); }
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

channelsApi.MapPost("/{connectionId:guid}/bot-webhook", async (Guid organizationId, Guid connectionId,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IDataProtectionProvider protection,
    IHttpClientFactory clients, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.SocialChannelConnections.SingleOrDefaultAsync(x =>
        x.Id == connectionId && x.OrganizationId == organizationId && (x.Provider == "Bale" || x.Provider == "Rubika"), ct);
    if (item is null) return Results.NotFound();
    if (item.Status != "Connected" || string.IsNullOrWhiteSpace(item.EncryptedAccessToken))
        return Results.Conflict(new { error = "ابتدا توکن ربات را با آزمون زنده تأیید کنید." });
    var publicBaseUrl = config["PublicBaseUrl"]?.TrimEnd('/');
    if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri) ||
        (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
        return Results.BadRequest(new { error = "PublicBaseUrl باید آدرس عمومی HTTPS سامانه باشد." });
    var signingKey = config["Jwt:SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey)) return Results.Problem("Webhook signing is not configured.", statusCode: 503);
    var signature = BotWebhookSignature(signingKey, connectionId, item.Provider);
    var webhookUrl = $"{publicBaseUrl}/api/webhooks/{item.Provider.ToLowerInvariant()}/{connectionId:D}/{signature}";
    string botToken;
    try { botToken = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(item.EncryptedAccessToken); }
    catch (CryptographicException) { return Results.Conflict(new { error = "توکن ربات قابل رمزگشایی نیست؛ اتصال را دوباره ثبت کنید." }); }
    try
    {
        HttpResponseMessage response;
        if (item.Provider == "Bale")
        {
            response = await clients.CreateClient("TelegramBotApi").PostAsJsonAsync(
                $"https://tapi.bale.ai/bot{botToken}/setWebhook",
                new { url = webhookUrl, allowed_updates = new[] { "message", "edited_message" }, drop_pending_updates = false }, ct);
        }
        else
        {
            response = await clients.CreateClient("TelegramBotApi").PostAsJsonAsync(
                $"https://botapi.rubika.ir/v3/{botToken}/updateBotEndpoints",
                new { url = webhookUrl, type = "ReceiveUpdate" }, ct);
        }
        using (response)
        using (var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct))
        {
            var root = payload.RootElement;
            var ok = item.Provider == "Bale"
                ? root.TryGetProperty("ok", out var baleOk) && baleOk.ValueKind == JsonValueKind.True
                : root.TryGetProperty("status", out var rubikaStatus) && rubikaStatus.GetString() == "OK";
            if (!response.IsSuccessStatusCode || !ok)
                return Results.Problem($"سرویس {item.Provider} وب‌هوک را نپذیرفت؛ تنظیمات ربات و دامنه را بررسی کنید.", statusCode: 502);
        }
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { configured = true, webhookUrl, provider = item.Provider });
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    { return Results.Problem("ثبت وب‌هوک در زمان مقرر پاسخ نداد.", statusCode: 504); }
    catch (HttpRequestException)
    { return Results.Problem($"ارتباط با API پیام‌رسان {item.Provider} برقرار نشد.", statusCode: 502); }
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
        x => x.Id == connectionId && x.OrganizationId == organizationId && (x.Provider == "Telegram" || x.Provider == "Bale" || x.Provider == "Rubika"), ct);
    if (item is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(item.EncryptedAccessToken))
        return Results.BadRequest(new { error = "برای این کانال توکن ربات ثبت نشده است." });
    if (item.Provider is "Bale" or "Rubika")
    {
        string verifiedBotToken;
        try { verifiedBotToken = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(item.EncryptedAccessToken); }
        catch (CryptographicException) { return Results.Conflict(new { error = "توکن ربات قابل رمزگشایی نیست؛ اعتبارنامه را دوباره ثبت کنید." }); }
        try
        {
            var url = item.Provider == "Bale"
                ? $"https://tapi.bale.ai/bot{verifiedBotToken}/getMe"
                : $"https://botapi.rubika.ir/v3/{verifiedBotToken}/getMe";
            using var response = await httpClientFactory.CreateClient("TelegramBotApi").PostAsJsonAsync(url, new { }, ct);
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = payload.RootElement;
            var ok = item.Provider == "Bale"
                ? response.IsSuccessStatusCode && root.TryGetProperty("ok", out var baleOk) && baleOk.ValueKind == JsonValueKind.True
                : response.IsSuccessStatusCode && root.TryGetProperty("status", out var rubikaStatus) && rubikaStatus.GetString() == "OK";
            if (!ok)
            {
                item.Status = "VerificationFailed"; item.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
                return Results.BadRequest(new { error = $"توکن {item.Provider} تأیید نشد؛ توکن دریافتی از BotFather را بررسی کنید." });
            }
            var account = item.Provider == "Bale"
                ? (root.TryGetProperty("result", out var baleResult) ? baleResult : root)
                : (root.TryGetProperty("data", out var rubikaData) ? rubikaData : root);
            var id = account.TryGetProperty("id", out var accountId) ? accountId.ToString() : null;
            var username = account.TryGetProperty("username", out var userName) ? userName.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && await db.SocialChannelConnections.AnyAsync(x =>
                x.Id != item.Id && x.OrganizationId == organizationId && x.Provider == item.Provider && x.ExternalAccountId == id, ct))
                return Results.Conflict(new { error = "این ربات قبلاً در همین سازمان ثبت شده است." });
            if (!string.IsNullOrWhiteSpace(id)) item.ExternalAccountId = id;
            item.Status = "Connected"; item.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
            return Results.Ok(new { item.Id, item.Provider, item.DisplayName, item.ExternalAccountId, Username = username, item.Status, VerifiedAt = item.UpdatedAt });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Results.Problem($"ارتباط با API {item.Provider} timeout شد.", statusCode: 504); }
        catch (HttpRequestException)
        { return Results.Problem($"ارتباط با API {item.Provider} برقرار نشد.", statusCode: 502); }
    }

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
    ClaimsPrincipal principal, MajaziDbContext db, IDataProtectionProvider protection, IHttpClientFactory clients, IConfiguration config, CancellationToken ct) =>
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
        else if (item.Provider == "Bale") endpoint = $"https://tapi.bale.ai/bot{credential}/getMe";
        else if (item.Provider == "Rubika") endpoint = $"https://botapi.rubika.ir/v3/{credential}/getMe";
        else if (item.Provider == "Instagram") endpoint = QueryHelpers.AddQueryString("https://graph.instagram.com/me",
            new Dictionary<string, string?> { ["fields"] = "id,user_id,username", ["access_token"] = credential });
        else if (item.Provider == "Facebook")
        {
            if (string.IsNullOrWhiteSpace(item.ExternalAccountId)) return Results.Conflict(new { error = "برای Facebook باید شناسه صفحه ثبت شود." });
            var version = config["Meta:GraphApiVersion"] ?? "v22.0";
            endpoint = $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(item.ExternalAccountId)}?fields=id,name";
        }
        else if (item.Provider == "WhatsApp")
        {
            if (string.IsNullOrWhiteSpace(item.ExternalAccountId)) return Results.Conflict(new { error = "برای WhatsApp باید Phone Number ID ثبت شود." });
            var version = config["Meta:GraphApiVersion"] ?? "v22.0";
            endpoint = $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(item.ExternalAccountId)}?fields=id,display_phone_number,verified_name";
        }
        else return Results.Conflict(new { error = "برای این پلتفرم هنوز آداپتور رسمی بررسی سلامت پیاده‌سازی نشده است." });
        using var request = new HttpRequestMessage(item.Provider == "Rubika" ? HttpMethod.Post : HttpMethod.Get, endpoint);
        if (item.Provider is "WhatsApp" or "Facebook") request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        using var response = await clients.CreateClient(item.Provider is "Telegram" or "Bale" or "Rubika" ? "TelegramBotApi" : "MetaOAuth").SendAsync(request, ct);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = payload.RootElement;
        var providerOk = response.IsSuccessStatusCode &&
            (item.Provider is "Telegram" or "Bale"
                ? root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                : item.Provider == "Rubika"
                    ? root.TryGetProperty("status", out var rubikaStatus) && rubikaStatus.GetString() == "OK"
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
        var result = root.TryGetProperty("result", out var telegramResult) ? telegramResult :
            root.TryGetProperty("data", out var rubikaData) ? rubikaData : root;
        if (result.TryGetProperty("id", out var externalId)) item.ExternalAccountId = externalId.ToString();
        var accountName = result.TryGetProperty("username", out var username) ? username.GetString() : result.TryGetProperty("name", out var pageName) ? pageName.GetString() : result.TryGetProperty("verified_name", out var verifiedName) ? verifiedName.GetString() : item.DisplayName;
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
    RecordAuditEvent(db, organizationId, userId.Value, "channel.removed", "SocialChannelConnection", connectionId, "اتصال کانال اجتماعی حذف شد.", new { item.Provider });
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


dashboardApi.MapGet("/reports", async (Guid organizationId, DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var start = (from ?? DateTimeOffset.UtcNow.AddDays(-29)).ToUniversalTime();
    var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
    if (end < start || (end - start).TotalDays > 366) return Results.BadRequest(new { error = "بازه گزارش نامعتبر است؛ حداکثر بازه ۳۶۶ روز است." });
    var endExclusive = end.AddDays(1);
    var contacts = await db.CrmContacts.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.CreatedAt }).ToListAsync(ct);
    var leads = await db.CrmLeads.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.CreatedAt, x.Stage, x.EstimatedValue }).ToListAsync(ct);
    var activities = await db.CrmActivities.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.OccurredAt >= start && x.OccurredAt < endExclusive)
        .Select(x => new { x.OccurredAt, x.ActivityType }).ToListAsync(ct);
    var campaigns = await db.MarketingCampaigns.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.CreatedAt, x.Status, x.Budget, x.ActualSpend, x.AttributedRevenue, x.Impressions, x.Clicks, x.Conversions }).ToListAsync(ct);
    var drafts = await db.ContentDrafts.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.CreatedAt, x.Status, x.Channel }).ToListAsync(ct);
    var messages = await db.InboxMessages.AsNoTracking().Where(x => x.Conversation!.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.CreatedAt, x.Direction, x.Status }).ToListAsync(ct);
    var orderRows = await db.SalesOrders.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.Id, x.CreatedAt, x.Status, x.Currency, x.Total }).ToListAsync(ct);
    var orderIds = orderRows.Select(x => x.Id).ToArray();
    var orderPayments = await db.PaymentTransactions.AsNoTracking()
        .Where(x => orderIds.Contains(x.OrderId) && x.CreatedAt >= start && x.CreatedAt < endExclusive)
        .Select(x => new { x.OrderId, x.Amount, x.Status }).ToListAsync(ct);
    var paidByOrder = orderPayments.Where(x => x.Status == "Paid").GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
    var refundedByOrder = orderPayments.Where(x => x.Status == "Refunded").GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
    var orderFinancialsByCurrency = orderRows.GroupBy(x => x.Currency).Select(g => new
    {
        currency = g.Key,
        orders = g.Count(),
        orderValue = g.Sum(x => x.Total),
        paidAmount = g.Sum(x => paidByOrder.GetValueOrDefault(x.Id)),
        refundedAmount = g.Sum(x => refundedByOrder.GetValueOrDefault(x.Id)),
        outstandingBalance = g.Sum(x => Math.Max(0, x.Total - paidByOrder.GetValueOrDefault(x.Id) + refundedByOrder.GetValueOrDefault(x.Id)))
    }).OrderBy(x => x.currency).ToList();
    var daily = Enumerable.Range(0, (int)Math.Min(367, Math.Floor((end.Date - start.Date).TotalDays) + 1))
        .Select(offset => start.Date.AddDays(offset))
        .Select(day => new
        {
            date = day.ToString("yyyy-MM-dd"),
            contacts = contacts.Count(x => x.CreatedAt.UtcDateTime.Date == day.Date),
            leads = leads.Count(x => x.CreatedAt.UtcDateTime.Date == day.Date),
            activities = activities.Count(x => x.OccurredAt.UtcDateTime.Date == day.Date),
            campaigns = campaigns.Count(x => x.CreatedAt.UtcDateTime.Date == day.Date),
            contentDrafts = drafts.Count(x => x.CreatedAt.UtcDateTime.Date == day.Date),
            messages = messages.Count(x => x.CreatedAt.UtcDateTime.Date == day.Date),
            orders = orderRows.Count(x => x.CreatedAt.UtcDateTime.Date == day.Date)
        }).ToArray();
    return Results.Ok(new
    {
        from = start, to = end,
        totals = new
        {
            contacts = contacts.Count, leads = leads.Count, activities = activities.Count,
            campaigns = campaigns.Count, contentDrafts = drafts.Count, messages = messages.Count,
            inboundMessages = messages.Count(x => x.Direction == "Inbound"),
            outboundMessages = messages.Count(x => x.Direction == "Outbound"),
            estimatedPipeline = leads.Where(x => x.Stage != "Lost" && x.Stage != "Won").Sum(x => x.EstimatedValue ?? 0m),
            wonValue = leads.Where(x => x.Stage == "Won").Sum(x => x.EstimatedValue ?? 0m),
            budget = campaigns.Sum(x => x.Budget), spend = campaigns.Sum(x => x.ActualSpend),
            attributedRevenue = campaigns.Sum(x => x.AttributedRevenue),
            impressions = campaigns.Sum(x => x.Impressions), clicks = campaigns.Sum(x => x.Clicks),
            conversions = campaigns.Sum(x => x.Conversions),
            orders = orderRows.Count,
            draftOrders = orderRows.Count(x => x.Status == "Draft"),
            confirmedOrders = orderRows.Count(x => x.Status == "Confirmed"),
            processingOrders = orderRows.Count(x => x.Status == "Processing"),
            completedOrders = orderRows.Count(x => x.Status == "Completed"),
            cancelledOrders = orderRows.Count(x => x.Status == "Cancelled"),
            orderFinancialsByCurrency
        },
        leadStages = leads.GroupBy(x => x.Stage).Select(g => new { key = g.Key, count = g.Count(), value = g.Sum(x => x.EstimatedValue ?? 0m) }).OrderByDescending(x => x.count),
        campaignStatuses = campaigns.GroupBy(x => x.Status).Select(g => new { key = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
        contentStatuses = drafts.GroupBy(x => x.Status).Select(g => new { key = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
        daily
    });
});


// Meta WhatsApp Cloud API webhook verification and event ingestion.
app.MapGet("/api/webhooks/whatsapp", (HttpRequest request, IConfiguration config) =>
{
    var mode = request.Query["hub.mode"].ToString();
    var token = request.Query["hub.verify_token"].ToString();
    var challenge = request.Query["hub.challenge"].ToString();
    var expected = config["Meta:WebhookVerifyToken"];
    if (!string.Equals(mode, "subscribe", StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(expected) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
        return Results.Unauthorized();
    return Results.Text(challenge, "text/plain");
});

// Shared Meta webhook endpoint for Instagram Messaging and Facebook Messenger.
app.MapGet("/api/webhooks/meta", (HttpRequest request, IConfiguration config) =>
{
    var mode = request.Query["hub.mode"].ToString();
    var token = request.Query["hub.verify_token"].ToString();
    var challenge = request.Query["hub.challenge"].ToString();
    var expected = config["Meta:WebhookVerifyToken"];
    if (!string.Equals(mode, "subscribe", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(expected) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
        return Results.Unauthorized();
    return Results.Text(challenge, "text/plain");
});

app.MapPost("/api/webhooks/meta", async (HttpRequest request, MajaziDbContext db, IConfiguration config, CancellationToken ct) =>
{
    var secret = config["Meta:AppSecret"];
    if (string.IsNullOrWhiteSpace(secret)) return Results.Problem("Meta webhook signature validation is not configured.", statusCode: 503);
    var supplied = request.Headers["X-Hub-Signature-256"].ToString();
    if (!supplied.StartsWith("sha256=", StringComparison.Ordinal)) return Results.Unauthorized();
    await using var stream = new MemoryStream();
    await request.Body.CopyToAsync(stream, ct);
    var raw = stream.ToArray();
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(raw)).ToLowerInvariant();
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        return Results.Unauthorized();

    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;
    var objectName = root.TryGetProperty("object", out var obj) ? obj.GetString() : null;
    var provider = objectName switch { "instagram" => "Instagram", "page" => "Facebook", _ => null };
    if (provider is null) return Results.Ok(new { accepted = true, ignored = true });
    if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        return Results.Ok(new { accepted = true, ignored = true });

    foreach (var entry in entries.EnumerateArray())
    {
        var accountId = entry.TryGetProperty("id", out var entryId) ? entryId.ToString() : null;
        if (string.IsNullOrWhiteSpace(accountId)) continue;
        var channel = await db.SocialChannelConnections.SingleOrDefaultAsync(
            x => x.Provider == provider && x.ExternalAccountId == accountId && x.Status == "Connected", ct);
        if (channel is null || !entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array) continue;
        foreach (var evt in events.EnumerateArray())
        {
            var senderId = evt.TryGetProperty("sender", out var sender) && sender.TryGetProperty("id", out var sid) ? sid.ToString() : null;
            if (string.IsNullOrWhiteSpace(senderId)) continue;
            if (evt.TryGetProperty("delivery", out var delivery) && delivery.TryGetProperty("mids", out var mids) && mids.ValueKind == JsonValueKind.Array)
            {
                foreach (var mid in mids.EnumerateArray())
                {
                    var externalId = mid.ToString();
                    var sent = await db.InboxMessages.Include(x => x.Conversation)
                        .SingleOrDefaultAsync(x => x.ExternalMessageId == externalId && x.Direction == "Outbound" &&
                            x.Conversation!.ChannelConnectionId == channel.Id, ct);
                    if (sent is not null) sent.Status = "Delivered";
                }
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (evt.TryGetProperty("read", out var readEvent) && readEvent.TryGetProperty("watermark", out _))
            {
                // A read watermark is a provider timestamp; update only outbound messages in this conversation.
                var conversationForRead = await db.InboxConversations.SingleOrDefaultAsync(
                    x => x.ChannelConnectionId == channel.Id && x.ExternalConversationId == senderId, ct);
                if (conversationForRead is not null)
                {
                    var watermark = readEvent.GetProperty("watermark").GetInt64();
                    var readAt = DateTimeOffset.FromUnixTimeMilliseconds(watermark);
                    var outgoing = await db.InboxMessages.Where(x => x.ConversationId == conversationForRead.Id &&
                        x.Direction == "Outbound" && x.CreatedAt <= readAt && (x.Status == "Sent" || x.Status == "Delivered")).ToListAsync(ct);
                    foreach (var sent in outgoing) sent.Status = "Read";
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }
            if (!evt.TryGetProperty("message", out var incoming)) continue;
            if (incoming.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue;
            var externalMessageId = incoming.TryGetProperty("mid", out var midElement) ? midElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(externalMessageId)) continue;
            var messageText = incoming.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            var attachmentSummary = incoming.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array
                ? string.Join("، ", attachments.EnumerateArray().Select(a => a.TryGetProperty("type", out var t) ? t.GetString() : "رسانه"))
                : null;
            var body = !string.IsNullOrWhiteSpace(messageText) ? messageText :
                !string.IsNullOrWhiteSpace(attachmentSummary) ? $"[پیوست {provider}: {attachmentSummary}]" : "[پیام بدون متن]";
            if (body.Length > 10000) body = body[..10000];

            var conversation = await db.InboxConversations.SingleOrDefaultAsync(
                x => x.ChannelConnectionId == channel.Id && x.ExternalConversationId == senderId, ct);
            if (conversation is null)
            {
                conversation = new InboxConversation
                {
                    OrganizationId = channel.OrganizationId, ChannelConnectionId = channel.Id,
                    ExternalConversationId = senderId, Subject = $"{provider} {senderId}", Status = "Open"
                };
                db.InboxConversations.Add(conversation);
                await db.SaveChangesAsync(ct);
            }
            if (await db.InboxMessages.AnyAsync(x => x.ConversationId == conversation.Id &&
                x.ExternalMessageId == externalMessageId, ct)) continue;
            var receivedAt = DateTimeOffset.UtcNow;
            if (evt.TryGetProperty("timestamp", out var timestamp) && timestamp.TryGetInt64(out var ms))
            {
                try { receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch (ArgumentOutOfRangeException) { }
            }
            var inboxMessage = new InboxMessage
            {
                ConversationId = conversation.Id, ExternalMessageId = externalMessageId,
                Direction = "Inbound", Body = body, Status = "Received", CreatedAt = receivedAt,
                ProviderPayload = JsonSerializer.Serialize(evt)
            };
            db.InboxMessages.Add(inboxMessage);
            await QueueAutomationRunsAsync(db, channel.OrganizationId, "InboxMessageReceived", "InboxMessage",
                inboxMessage.Id, new { messageBody = body, conversationSubject = conversation.Subject, channel = provider }, ct);
            conversation.LastMessageAt = receivedAt;
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            if (conversation.Status.Equals("resolved", StringComparison.OrdinalIgnoreCase)) conversation.Status = "open";
            await db.SaveChangesAsync(ct);
        }
    }
    return Results.Ok(new { accepted = true });
});

app.MapPost("/api/webhooks/whatsapp", async (HttpRequest request, MajaziDbContext db,
    IConfiguration config, CancellationToken ct) =>
{
    var appSecret = config["Meta:AppSecret"];
    if (string.IsNullOrWhiteSpace(appSecret)) return Results.Problem("Meta webhook signature validation is not configured.", statusCode: 503);
    var supplied = request.Headers["X-Hub-Signature-256"].ToString();
    if (!supplied.StartsWith("sha256=", StringComparison.Ordinal)) return Results.Unauthorized();
    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, ct);
    var raw = buffer.ToArray();
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
    var expectedSignature = "sha256=" + Convert.ToHexString(hmac.ComputeHash(raw)).ToLowerInvariant();
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);
    if (suppliedBytes.Length != expectedBytes.Length ||
        !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes)) return Results.Unauthorized();

    using var document = JsonDocument.Parse(raw);
    var root = document.RootElement;
    if (!root.TryGetProperty("object", out var objectElement) || objectElement.GetString() != "whatsapp_business_account")
        return Results.Ok(new { accepted = true, ignored = true });

    if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
    foreach (var entry in entries.EnumerateArray())
    {
        if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
        foreach (var change in changes.EnumerateArray())
        {
            if (!change.TryGetProperty("value", out var value)) continue;
            var phoneNumberId = value.TryGetProperty("metadata", out var metadata) &&
                metadata.TryGetProperty("phone_number_id", out var phoneId) ? phoneId.GetString() : null;
            if (string.IsNullOrWhiteSpace(phoneNumberId)) continue;
            var channel = await db.SocialChannelConnections.SingleOrDefaultAsync(
                x => x.Provider == "WhatsApp" && x.ExternalAccountId == phoneNumberId && x.Status == "Connected", ct);
            if (channel is null) continue;

            if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
            foreach (var statusItem in statuses.EnumerateArray())
            {
                var messageId = statusItem.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var state = statusItem.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(state)) continue;
                var outboundMessage = await db.InboxMessages
                    .Include(x => x.Conversation)
                    .Where(x => x.ExternalMessageId == messageId && x.Direction == "Outbound" &&
                        x.Conversation!.ChannelConnectionId == channel.Id)
                    .SingleOrDefaultAsync(ct);
                if (outboundMessage is null) continue;
                outboundMessage.Status = state.ToLowerInvariant() switch
                {
                    "sent" => "Sent",
                    "delivered" => "Delivered",
                    "read" => "Read",
                    "failed" => "Failed",
                    _ => outboundMessage.Status
                };
                if (statusItem.TryGetProperty("errors", out var errors))
                    outboundMessage.ProviderPayload = JsonSerializer.Serialize(new { status = state, errors });
                await db.SaveChangesAsync(ct);
            }

            if (!value.TryGetProperty("messages", out var incoming) || incoming.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in incoming.EnumerateArray())
            {
                var externalMessageId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
                var sender = item.TryGetProperty("from", out var from) ? from.GetString() : null;
                var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "unknown";
                if (string.IsNullOrWhiteSpace(externalMessageId) || string.IsNullOrWhiteSpace(sender) ||
                    sender.Length > 32 || !sender.All(char.IsAsciiDigit)) continue;

                var body = type switch
                {
                    "text" when item.TryGetProperty("text", out var textNode) && textNode.TryGetProperty("body", out var textBody) => textBody.GetString(),
                    "image" => "[تصویر واتساپ]",
                    "audio" => "[پیام صوتی واتساپ]",
                    "video" => "[ویدئوی واتساپ]",
                    "document" => item.TryGetProperty("document", out var doc) && doc.TryGetProperty("filename", out var fileName)
                        ? $"[سند واتساپ: {fileName.GetString()}]" : "[سند واتساپ]",
                    "sticker" => "[استیکر واتساپ]",
                    "location" => "[موقعیت مکانی واتساپ]",
                    "contacts" => "[مخاطب ارسالی در واتساپ]",
                    "button" => item.TryGetProperty("button", out var button) && button.TryGetProperty("text", out var buttonText)
                        ? buttonText.GetString() : "[پاسخ دکمه‌ای واتساپ]",
                    "interactive" => "[پاسخ تعاملی واتساپ]",
                    _ => $"[پیام {type ?? "ناشناخته"} واتساپ]"
                };
                if (string.IsNullOrWhiteSpace(body)) body = $"[پیام {type ?? "ناشناخته"} واتساپ]";
                if (body.Length > 10000) body = body[..10000];

                var conversation = await db.InboxConversations.SingleOrDefaultAsync(
                    x => x.ChannelConnectionId == channel.Id && x.ExternalConversationId == sender, ct);
                if (conversation is null)
                {
                    var displayName = value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array &&
                        contacts.GetArrayLength() > 0 && contacts[0].TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("name", out var name) ? name.GetString() : null;
                    conversation = new InboxConversation
                    {
                        OrganizationId = channel.OrganizationId, ChannelConnectionId = channel.Id,
                        ExternalConversationId = sender,
                        Subject = string.IsNullOrWhiteSpace(displayName) ? $"WhatsApp {sender}" : displayName,
                        Status = "Open"
                    };
                    db.InboxConversations.Add(conversation);
                    await db.SaveChangesAsync(ct);
                }
                var duplicate = await db.InboxMessages.AnyAsync(
                    x => x.ConversationId == conversation.Id && x.ExternalMessageId == externalMessageId, ct);
                if (duplicate) continue;
                var receivedAt = DateTimeOffset.UtcNow;
                if (item.TryGetProperty("timestamp", out var timestamp) &&
                    long.TryParse(timestamp.ToString(), out var unixSeconds))
                {
                    try { receivedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
                    catch (ArgumentOutOfRangeException) { }
                }
                var inboxMessage = new InboxMessage
                {
                    ConversationId = conversation.Id, ExternalMessageId = externalMessageId,
                    Direction = "Inbound", Body = body, Status = "Received", CreatedAt = receivedAt,
                    ProviderPayload = JsonSerializer.Serialize(item)
                };
                db.InboxMessages.Add(inboxMessage);
                await QueueAutomationRunsAsync(db, channel.OrganizationId, "InboxMessageReceived", "InboxMessage",
                    inboxMessage.Id, new { messageBody = body, conversationSubject = conversation.Subject, channel = channel.Provider }, ct);
                conversation.LastMessageAt = receivedAt;
                conversation.UpdatedAt = DateTimeOffset.UtcNow;
                if (conversation.Status.Equals("resolved", StringComparison.OrdinalIgnoreCase)) conversation.Status = "open";
                await db.SaveChangesAsync(ct);
            }
        }
    }
    return Results.Ok(new { accepted = true });
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
    var inboxMessage = new InboxMessage {
        ConversationId = conversation.Id, ExternalMessageId = externalMessageId,
        Direction = "Inbound", Body = body, Status = "Received", CreatedAt = receivedAt
    };
    db.InboxMessages.Add(inboxMessage);
    await QueueAutomationRunsAsync(db, channel.OrganizationId, "InboxMessageReceived", "InboxMessage", inboxMessage.Id,
        new { messageBody = body, conversationSubject = conversation.Subject, channel = channel.Provider }, ct);
    conversation.LastMessageAt = receivedAt;
    conversation.UpdatedAt = DateTimeOffset.UtcNow;
    if (conversation.Status.Equals("resolved", StringComparison.OrdinalIgnoreCase)) conversation.Status = "open";
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { accepted = true });
});


app.MapPost("/api/webhooks/bale/{connectionId:guid}/{signature}", async (Guid connectionId, string signature,
    HttpRequest request, MajaziDbContext db, IConfiguration config, CancellationToken ct) =>
    await ProcessIranianBotWebhookAsync("Bale", connectionId, signature, request, db, config, ct));
app.MapPost("/api/webhooks/rubika/{connectionId:guid}/{signature}", async (Guid connectionId, string signature,
    HttpRequest request, MajaziDbContext db, IConfiguration config, CancellationToken ct) =>
    await ProcessIranianBotWebhookAsync("Rubika", connectionId, signature, request, db, config, ct));

var aiApi = app.MapGroup("/api/organizations/{organizationId:guid}/ai").RequireAuthorization();

aiApi.MapGet("/settings", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    IConfiguration config, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.AsNoTracking()
        .SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    var saved = await db.OrganizationAiProviderSettings.AsNoTracking()
        .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
    var baseUrl = saved?.BaseUrl ?? config["AI:BaseUrl"];
    var model = saved?.Model ?? config["AI:Model"];
    var keyConfigured = !string.IsNullOrWhiteSpace(saved?.EncryptedApiKey) || !string.IsNullOrWhiteSpace(config["AI:ApiKey"]);
    return Results.Ok(new
    {
        baseUrl, model, keyConfigured, isEnabled = saved?.IsEnabled ?? true,
        source = saved is null ? "server-environment" : "organization"
    });
});

aiApi.MapPut("/settings", async (Guid organizationId, OrganizationAiProviderSettingsRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(
        x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();

    var baseUrl = request.BaseUrl?.Trim().TrimEnd('/');
    var model = request.Model?.Trim();
    if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 500 ||
        !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
        (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback) ||
        !string.IsNullOrEmpty(endpoint.UserInfo) ||
        string.IsNullOrWhiteSpace(model) || model.Length > 160 ||
        (request.ApiKey?.Length ?? 0) > 8000 ||
        (!string.IsNullOrWhiteSpace(request.ApiKey) && request.ApiKey.Trim().Length < 8))
        return Results.BadRequest(new { error = "نشانی امن سرویس، نام مدل یا کلید API معتبر نیست." });

    var saved = await db.OrganizationAiProviderSettings.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
    if (saved is null)
    {
        saved = new OrganizationAiProviderSetting { OrganizationId = organizationId, BaseUrl = baseUrl, Model = model, UpdatedByUserId = userId.Value };
        db.OrganizationAiProviderSettings.Add(saved);
    }
    saved.BaseUrl = baseUrl;
    saved.Model = model;
    saved.IsEnabled = request.IsEnabled;
    saved.UpdatedByUserId = userId.Value;
    saved.UpdatedAt = DateTimeOffset.UtcNow;
    if (request.ClearApiKey) saved.EncryptedApiKey = null;
    else if (!string.IsNullOrWhiteSpace(request.ApiKey))
        saved.EncryptedApiKey = protection.CreateProtector("Majazi.AIProviderSettings.v1", organizationId.ToString("N")).Protect(request.ApiKey.Trim());

    RecordAuditEvent(db, organizationId, userId.Value, "ai.settings_updated", "AiProviderSettings", saved.Id,
        "تنظیمات اتصال هوش مصنوعی سازمان به‌روزرسانی شد.", new { saved.BaseUrl, saved.Model, saved.IsEnabled, keyChanged = !string.IsNullOrWhiteSpace(request.ApiKey) || request.ClearApiKey });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { saved.BaseUrl, saved.Model, isEnabled = saved.IsEnabled, keyConfigured = !string.IsNullOrWhiteSpace(saved.EncryptedApiKey) });
});

aiApi.MapPost("/test", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    IDataProtectionProvider protection, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.AsNoTracking()
        .SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var provider = await ResolveAiProviderConfigAsync(db, protection, config, organizationId, ct);
    if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.ApiKey) || string.IsNullOrWhiteSpace(provider.BaseUrl) || string.IsNullOrWhiteSpace(provider.Model))
        return Results.Problem("تنظیمات فعال و کلید API معتبر برای این سازمان ثبت نشده است.", statusCode: 503);
    try
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = provider.Model, temperature = 0,
            messages = new[] { new { role = "user", content = "Reply with exactly: MAJAZI_OK" } },
            max_tokens = 12
        });
        using var response = await clients.CreateClient("AIProvider").SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            return Results.Problem("آزمون اتصال ناموفق بود (HTTP " + (int)response.StatusCode + "). کلید، مدل و نشانی API را بررسی کنید.", statusCode: 502);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var output = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return Results.Ok(new { success = !string.IsNullOrWhiteSpace(output), model = provider.Model, message = "ارتباط با مدل برقرار شد." });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception)
    {
        return Results.Problem("ارتباط با سرویس برقرار نشد؛ شبکه خروجی، پراکسی و تنظیمات API را بررسی کنید.", statusCode: 502);
    }
});

aiApi.MapPost("/generate-text", async (Guid organizationId, GenerateAssistantTextRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IDataProtectionProvider protection, IHttpClientFactory clients, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var module = request.Module?.Trim().ToLowerInvariant();
    var field = request.Field?.Trim().ToLowerInvariant();
    var context = request.Context?.Trim();
    var currentText = request.CurrentText?.Trim();
    var instructions = request.Instructions?.Trim();
    var allowed = new Dictionary<string, HashSet<string>>
    {
        ["crm"] = ["contact.notes", "lead.notes", "activity.summary"],
        ["campaign"] = ["objective", "notes"],
        ["inbox"] = ["conversation.subject", "message.reply"],
        ["content"] = ["title", "body", "description"],
        ["knowledge"] = ["article.summary", "article.body"]
    };
    if (module is null || field is null || !allowed.TryGetValue(module, out var fields) || !fields.Contains(field) ||
        string.IsNullOrWhiteSpace(context) || context.Length > 1500 || (currentText?.Length ?? 0) > 5000 ||
        (instructions?.Length ?? 0) > 2000)
        return Results.BadRequest(new { error = "نوع متن یا اطلاعات زمینه معتبر نیست." });
    var provider = await ResolveAiProviderConfigAsync(db, protection, config, organizationId, ct);
    var apiKey = provider.ApiKey;
    var baseUrl = provider.BaseUrl?.TrimEnd('/');
    var model = provider.Model;
    if (!provider.Enabled || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl) ||
        !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback || string.IsNullOrWhiteSpace(model))
        return Results.Problem("اتصال هوش مصنوعی برای این سازمان پیکربندی یا فعال نشده است.", statusCode: 503);
    var systemPrompt = "شما دستیار حرفه‌ای نگارش فارسی برای یک سامانه مدیریت کسب‌وکار هستید. متن روشن، دقیق، طبیعی، کاربردی و متناسب با زمینه بنویسید. اطلاعات، عدد، نام، وعده یا ادعای تأییدنشده اختراع نکنید. خروجی را فقط به شکل JSON با کلید text برگردانید.";
    var userPrompt = $"ماژول: {module}\nفیلد: {field}\nزمینه: {context}\nمتن فعلی برای بازنویسی: {currentText ?? "وجود ندارد"}\nدستور کاربر: {instructions ?? "یک متن آماده و حرفه‌ای بنویس."}\nفقط متن نهایی قابل ویرایش را تولید کن.";
    try
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            model, temperature = 0.55,
            response_format = new { type = "json_object" },
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        });
        using var response = await clients.CreateClient("AIProvider").SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            return Results.Problem("سرویس هوش مصنوعی پاسخ موفق نداد (" + (int)response.StatusCode + ").", statusCode: 502);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var generated = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(generated)) return Results.Problem("پاسخ مدل خالی بود.", statusCode: 502);
        using var output = JsonDocument.Parse(generated);
        var text = output.RootElement.TryGetProperty("text", out var node) ? node.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 5000)
            return Results.Problem("متن تولیدشده معتبر نیست.", statusCode: 502);
        return Results.Ok(new { text, model });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception)
    {
        return Results.Problem("تولید متن با هوش مصنوعی انجام نشد؛ تنظیمات مدل و دسترسی سرویس را بررسی کنید.", statusCode: 502);
    }
});

var defaultTextTemplates = new Dictionary<string, (string Name, string Content)[]>
{
    ["crm|contact.notes"] = [("یادداشت پیگیری مخاطب", "مخاطب از طریق [منبع آشنایی] با مجموعه آشنا شده است. نیاز اصلی: [نیاز مخاطب]. اقدام بعدی: [اقدام و زمان پیگیری].")],
    ["crm|lead.notes"] = [("خلاصه فرصت فروش", "نیاز مشتری: [شرح نیاز]. راهکار پیشنهادی: [راهکار]. ارزش و اولویت فرصت: [جزئیات]. گام بعدی: [اقدام، مسئول و موعد].")],
    ["crm|activity.summary"] = [("گزارش تماس", "تماس با [نام مخاطب] درباره [موضوع] انجام شد. نتیجه: [نتیجه گفتگو]. اقدام بعدی: [اقدام و زمان].")],
    ["campaign|objective"] = [("هدف کمپین فروش", "افزایش آگاهی و جذب مخاطبان واجد شرایط برای [محصول/خدمت] و تبدیل آن‌ها به سرنخ و فروش قابل اندازه‌گیری در بازه [زمان].")],
    ["campaign|notes"] = [("چک‌لیست اجرای کمپین", "مخاطب هدف: [گروه مخاطب]. پیام اصلی: [پیام]. دارایی‌های موردنیاز: [محتوا و خلاقه]. شاخص‌های سنجش: [KPI]. مسئول اجرا: [نام].")],
    ["inbox|conversation.subject"] = [("عنوان پیگیری مشتری", "پیگیری درخواست [موضوع] - [نام مخاطب]")],
    ["inbox|message.reply"] = [("پاسخ اولیه پشتیبانی", "سلام، پیام شما دریافت شد. برای بررسی دقیق‌تر، لطفاً [اطلاعات تکمیلی موردنیاز] را ارسال کنید. پس از بررسی، نتیجه را اطلاع می‌دهیم.")],
    ["content|title"] = [("عنوان معرفی محصول", "معرفی [نام محصول]؛ راهکاری برای [نیاز مخاطب]")],
    ["content|body"] = [("متن معرفی محصول", "[نام محصول/خدمت] برای افرادی طراحی شده است که به [نیاز اصلی] اهمیت می‌دهند. ویژگی‌های مهم: [ویژگی‌ها]. برای دریافت اطلاعات بیشتر، [دعوت به اقدام].")],
    ["content|description"] = [("توضیح کوتاه", "[نام محصول/خدمت] با تمرکز بر [مزیت اصلی] به شما کمک می‌کند [نتیجه مورد انتظار]. برای آشنایی بیشتر با جزئیات و شرایط، با ما در ارتباط باشید.")],
    ["knowledge|article.summary"] = [("خلاصه مستند", "هدف این سند: [هدف]. مخاطبان: [گروه هدف]. نکات کلیدی: [موارد اصلی]. نتیجه مورد انتظار: [نتیجه].")],
    ["knowledge|article.body"] = [("ساختار راهنمای اجرایی", "## هدف\n[هدف و دامنه سند]\n\n## مسئولیت‌ها\n[نقش‌ها و مسئولیت‌ها]\n\n## مراحل اجرا\n1. [مرحله اول]\n2. [مرحله دوم]\n3. [مرحله سوم]\n\n## کنترل کیفیت\n[معیارها و چک‌لیست]\n\n## منابع و نکات تکمیلی\n[منابع معتبر و نکات مهم]")]
};

var templatesApi = app.MapGroup("/api/organizations/{organizationId:guid}/text-templates").RequireAuthorization();
templatesApi.MapGet("/", async (Guid organizationId, string? module, string? field, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var keys = defaultTextTemplates.Keys.Where(key =>
    {
        var parts = key.Split('|', 2);
        return (string.IsNullOrWhiteSpace(module) || parts[0] == module) &&
               (string.IsNullOrWhiteSpace(field) || parts[1] == field);
    }).ToArray();
    foreach (var key in keys)
    {
        var parts = key.Split('|', 2);
        if (await db.TextTemplates.AnyAsync(x => x.OrganizationId == organizationId && x.Module == parts[0] && x.Field == parts[1], ct)) continue;
        var now = DateTimeOffset.UtcNow;
        db.TextTemplates.AddRange(defaultTextTemplates[key].Select((item, index) => new TextTemplate
        {
            OrganizationId = organizationId, Module = parts[0], Field = parts[1], Name = item.Name,
            Content = item.Content, SortOrder = (index + 1) * 10, IsActive = true, CreatedAt = now, UpdatedAt = now
        }));
    }
    if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    var query = db.TextTemplates.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(module)) query = query.Where(x => x.Module == module);
    if (!string.IsNullOrWhiteSpace(field)) query = query.Where(x => x.Field == field);
    var items = await query.OrderBy(x => x.Module).ThenBy(x => x.Field).ThenBy(x => x.SortOrder)
        .Select(x => new { x.Id, x.Module, x.Field, x.Name, x.Content, x.SortOrder, x.IsActive, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
    return Results.Ok(items);
});

templatesApi.MapPost("/", async (Guid organizationId, TextTemplateRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var module = request.Module?.Trim().ToLowerInvariant(); var field = request.Field?.Trim().ToLowerInvariant();
    var name = request.Name?.Trim(); var content = request.Content?.Trim();
    if (module is null || field is null || !defaultTextTemplates.ContainsKey(module + "|" + field) ||
        string.IsNullOrWhiteSpace(name) || name.Length > 120 || string.IsNullOrWhiteSpace(content) || content.Length > 8000 ||
        request.SortOrder < 0 || request.SortOrder > 10000)
        return Results.BadRequest(new { error = "ماژول، فیلد، نام، متن یا ترتیب قالب معتبر نیست." });
    if (await db.TextTemplates.AnyAsync(x => x.OrganizationId == organizationId && x.Module == module && x.Field == field && x.Name == name, ct))
        return Results.Conflict(new { error = "قالبی با این نام برای این فیلد وجود دارد." });
    var item = new TextTemplate { OrganizationId = organizationId, Module = module, Field = field, Name = name, Content = content, SortOrder = request.SortOrder, IsActive = request.IsActive };
    db.TextTemplates.Add(item); await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/text-templates/{item.Id}", new { item.Id, item.Module, item.Field, item.Name, item.Content, item.SortOrder, item.IsActive, item.CreatedAt, item.UpdatedAt });
});

templatesApi.MapPut("/{templateId:guid}", async (Guid organizationId, Guid templateId, UpdateTextTemplateRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.TextTemplates.SingleOrDefaultAsync(x => x.Id == templateId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    var name = request.Name?.Trim(); var content = request.Content?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || string.IsNullOrWhiteSpace(content) || content.Length > 8000 || request.SortOrder < 0 || request.SortOrder > 10000)
        return Results.BadRequest(new { error = "نام، متن یا ترتیب قالب معتبر نیست." });
    if (await db.TextTemplates.AnyAsync(x => x.Id != templateId && x.OrganizationId == organizationId && x.Module == item.Module && x.Field == item.Field && x.Name == name, ct))
        return Results.Conflict(new { error = "قالب دیگری با این نام برای این فیلد وجود دارد." });
    item.Name = name; item.Content = content; item.SortOrder = request.SortOrder; item.IsActive = request.IsActive; item.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Module, item.Field, item.Name, item.Content, item.SortOrder, item.IsActive, item.UpdatedAt });
});

templatesApi.MapDelete("/{templateId:guid}", async (Guid organizationId, Guid templateId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (membership.Role is not ("Owner" or "Admin")) return Results.Forbid();
    var item = await db.TextTemplates.SingleOrDefaultAsync(x => x.Id == templateId && x.OrganizationId == organizationId, ct);
    if (item is null) return Results.NotFound();
    item.IsActive = false; item.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

var auditApi = app.MapGroup("/api/organizations/{organizationId:guid}/audit").RequireAuthorization();
auditApi.MapGet("/events", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    string? q, string? action, int? page, int? pageSize, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var query = db.OrganizationAuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        query = query.Where(x => x.Summary.Contains(term) || x.EntityType.Contains(term) || x.Action.Contains(term));
    }
    var total = await query.CountAsync(ct);
    var currentPage = Math.Clamp(page ?? 1, 1, 10000);
    var take = Math.Clamp(pageSize ?? 50, 1, 100);
    var items = await query.OrderByDescending(x => x.CreatedAt).Skip((currentPage - 1) * take).Take(take)
        .Select(x => new
        {
            x.Id, x.Action, x.EntityType, x.EntityId, x.Summary, x.CreatedAt,
            x.ActorUserId,
            actorEmail = db.Users.Where(u => u.Id == x.ActorUserId).Select(u => u.Email).FirstOrDefault()
        }).ToListAsync(ct);
    var actions = await db.OrganizationAuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .Select(x => x.Action).Distinct().OrderBy(x => x).ToListAsync(ct);
    return Results.Ok(new { items, total, page = currentPage, pageSize = take, actions });
});

var knowledgeApi = app.MapGroup("/api/organizations/{organizationId:guid}/knowledge").RequireAuthorization();

knowledgeApi.MapGet("/articles", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    string? q, string? category, string? status, int? page, int? pageSize, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var query = db.KnowledgeArticles.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.Trim();
        query = query.Where(x => x.Title.Contains(term) || (x.Summary != null && x.Summary.Contains(term)) ||
            x.Body.Contains(term) || x.Tags.Contains(term) || x.Category.Contains(term));
    }
    if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
    if (string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status != "Archived");
    else if (status != "All") query = query.Where(x => x.Status == status);
    var total = await query.CountAsync(ct);
    var currentPage = Math.Clamp(page ?? 1, 1, 10000);
    var take = Math.Clamp(pageSize ?? 25, 1, 100);
    var items = await query.OrderByDescending(x => x.UpdatedAt)
        .Skip((currentPage - 1) * take).Take(take)
        .Select(x => new { x.Id, x.Title, x.Slug, x.Summary, x.Category, x.Tags, x.Status, x.Version,
            x.CreatedByUserId, x.UpdatedByUserId, x.PublishedAt, x.CreatedAt, x.UpdatedAt })
        .ToListAsync(ct);
    var categories = await db.KnowledgeArticles.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.Status != "Archived")
        .Select(x => x.Category).Distinct().OrderBy(x => x).ToListAsync(ct);
    return Results.Ok(new { items, total, page = currentPage, pageSize = take, categories });
});

knowledgeApi.MapGet("/articles/{articleId:guid}", async (Guid organizationId, Guid articleId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var article = await db.KnowledgeArticles.AsNoTracking()
        .Where(x => x.Id == articleId && x.OrganizationId == organizationId)
        .Select(x => new { x.Id, x.Title, x.Slug, x.Summary, x.Body, x.Category, x.Tags, x.Status, x.Version,
            x.CreatedByUserId, x.UpdatedByUserId, x.PublishedAt, x.CreatedAt, x.UpdatedAt })
        .SingleOrDefaultAsync(ct);
    return article is null ? Results.NotFound() : Results.Ok(article);
});

knowledgeApi.MapGet("/articles/{articleId:guid}/revisions", async (Guid organizationId, Guid articleId,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    if (!await db.KnowledgeArticles.AnyAsync(x => x.Id == articleId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
    var items = await db.KnowledgeArticleRevisions.AsNoTracking().Where(x => x.ArticleId == articleId)
        .OrderByDescending(x => x.Version).Take(100)
        .Select(x => new { x.Id, x.Version, x.Title, x.Summary, x.Body, x.Category, x.Tags, x.ChangeNote, x.CreatedByUserId, x.CreatedAt })
        .ToListAsync(ct);
    return Results.Ok(items);
});

knowledgeApi.MapPost("/articles", async (Guid organizationId, KnowledgeArticleRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var title = request.Title?.Trim(); var slug = request.Slug?.Trim().ToLowerInvariant();
    var body = request.Body?.Trim(); var category = request.Category?.Trim();
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || string.IsNullOrWhiteSpace(body) || body.Length > 60000 ||
        (request.Summary?.Length ?? 0) > 500 || string.IsNullOrWhiteSpace(category) || category.Length > 100 ||
        (request.Tags?.Length ?? 0) > 1000 || (request.ChangeNote?.Length ?? 0) > 300)
        return Results.BadRequest(new { error = "عنوان، متن، دسته‌بندی یا طول اطلاعات مقاله معتبر نیست." });
    if (string.IsNullOrWhiteSpace(slug)) slug = "article-" + Guid.NewGuid().ToString("N")[..12];
    if (slug.Length > 160 || !slug.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') || slug.StartsWith('-') || slug.EndsWith('-') || slug.Contains("--"))
        return Results.BadRequest(new { error = "شناسه مقاله باید فقط شامل حروف انگلیسی کوچک، عدد و خط تیره باشد." });
    if (await db.KnowledgeArticles.AnyAsync(x => x.OrganizationId == organizationId && x.Slug == slug, ct))
        return Results.Conflict(new { error = "این شناسه در همین سازمان قبلاً استفاده شده است." });
    var tags = string.Join(", ", (request.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => x.Length <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));
    var now = DateTimeOffset.UtcNow;
    var article = new KnowledgeArticle
    {
        OrganizationId = organizationId, Title = title, Slug = slug, Summary = Clean(request.Summary),
        Body = body, Category = category, Tags = tags, Status = "Draft",
        CreatedByUserId = userId.Value, UpdatedByUserId = userId.Value, CreatedAt = now, UpdatedAt = now, Version = 1
    };
    db.KnowledgeArticles.Add(article);
    db.KnowledgeArticleRevisions.Add(new KnowledgeArticleRevision
    {
        ArticleId = article.Id, Version = 1, Title = title, Summary = article.Summary ?? "",
        Body = body, Category = category, Tags = tags, ChangeNote = Clean(request.ChangeNote) ?? "نسخه اولیه",
        CreatedByUserId = userId.Value, CreatedAt = now
    });
    RecordAuditEvent(db, organizationId, userId.Value, "knowledge.created", "KnowledgeArticle", article.Id, $"مقاله «{title}» ایجاد شد.");
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/knowledge/articles/{article.Id}",
        new { article.Id, article.Title, article.Slug, article.Summary, article.Category, article.Tags,
            article.Status, article.Version, article.CreatedAt, article.UpdatedAt });
});

knowledgeApi.MapPut("/articles/{articleId:guid}", async (Guid organizationId, Guid articleId, KnowledgeArticleRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var article = await db.KnowledgeArticles.SingleOrDefaultAsync(x => x.Id == articleId && x.OrganizationId == organizationId, ct);
    if (article is null) return Results.NotFound();
    if (article.Status == "Archived") return Results.Conflict(new { error = "مقاله بایگانی شده است؛ ابتدا آن را به پیش‌نویس بازگردانید." });
    if (request.Version != article.Version) return Results.Conflict(new { error = "نسخه مقاله تغییر کرده است؛ اطلاعات را تازه‌سازی و دوباره ذخیره کنید.", currentVersion = article.Version });
    var title = request.Title?.Trim(); var slug = request.Slug?.Trim().ToLowerInvariant();
    var body = request.Body?.Trim(); var category = request.Category?.Trim();
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || string.IsNullOrWhiteSpace(body) || body.Length > 60000 ||
        string.IsNullOrWhiteSpace(slug) || slug.Length > 160 || !slug.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') ||
        slug.StartsWith('-') || slug.EndsWith('-') || slug.Contains("--") ||
        string.IsNullOrWhiteSpace(category) || category.Length > 100 ||
        (request.Summary?.Length ?? 0) > 500 || (request.Tags?.Length ?? 0) > 1000 || (request.ChangeNote?.Length ?? 0) > 300)
        return Results.BadRequest(new { error = "اطلاعات مقاله معتبر نیست." });
    if (await db.KnowledgeArticles.AnyAsync(x => x.OrganizationId == organizationId && x.Id != articleId && x.Slug == slug, ct))
        return Results.Conflict(new { error = "این شناسه در همین سازمان قبلاً استفاده شده است." });
    var tags = string.Join(", ", (request.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => x.Length <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));
    article.Title = title; article.Slug = slug; article.Summary = Clean(request.Summary);
    article.Body = body; article.Category = category; article.Tags = tags;
    article.Version++; article.UpdatedByUserId = userId.Value; article.UpdatedAt = DateTimeOffset.UtcNow;
    db.KnowledgeArticleRevisions.Add(new KnowledgeArticleRevision
    {
        ArticleId = article.Id, Version = article.Version, Title = article.Title, Summary = article.Summary ?? "",
        Body = article.Body, Category = article.Category, Tags = article.Tags,
        ChangeNote = Clean(request.ChangeNote) ?? "ویرایش مقاله", CreatedByUserId = userId.Value
    });
    RecordAuditEvent(db, organizationId, userId.Value, "knowledge.updated", "KnowledgeArticle", article.Id, $"مقاله «{article.Title}» به نسخه {article.Version} رسید.");
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { article.Id, article.Title, article.Slug, article.Summary, article.Category, article.Tags,
        article.Status, article.Version, article.PublishedAt, article.UpdatedAt });
});

knowledgeApi.MapPatch("/articles/{articleId:guid}/status", async (Guid organizationId, Guid articleId,
    KnowledgeArticleStatusRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.UserId == userId && x.OrganizationId == organizationId, ct);
    if (membership is null) return Results.NotFound();
    if (request.Status is not ("Draft" or "Published" or "Archived")) return Results.BadRequest(new { error = "وضعیت مقاله معتبر نیست." });
    if ((request.Status is "Published" or "Archived") && membership.Role is not ("Owner" or "Admin"))
        return Results.Forbid();
    var article = await db.KnowledgeArticles.SingleOrDefaultAsync(x => x.Id == articleId && x.OrganizationId == organizationId, ct);
    if (article is null) return Results.NotFound();
    article.Status = request.Status;
    article.PublishedAt = request.Status == "Published" ? article.PublishedAt ?? DateTimeOffset.UtcNow : null;
    article.UpdatedByUserId = userId.Value; article.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "knowledge.status_changed", "KnowledgeArticle", article.Id, $"وضعیت مقاله «{article.Title}» به {article.Status} تغییر کرد.");
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { article.Id, article.Status, article.PublishedAt, article.UpdatedAt });
});

knowledgeApi.MapPost("/articles/{articleId:guid}/revisions/{revisionId:guid}/restore", async (Guid organizationId, Guid articleId,
    Guid revisionId, KnowledgeRestoreRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var article = await db.KnowledgeArticles.SingleOrDefaultAsync(x => x.Id == articleId && x.OrganizationId == organizationId, ct);
    var revision = await db.KnowledgeArticleRevisions.SingleOrDefaultAsync(x => x.Id == revisionId && x.ArticleId == articleId, ct);
    if (article is null || revision is null) return Results.NotFound();
    if (article.Status == "Archived") return Results.Conflict(new { error = "ابتدا مقاله را از بایگانی خارج کنید." });
    if (request.Version != article.Version) return Results.Conflict(new { error = "نسخه مقاله تغییر کرده است؛ صفحه را تازه‌سازی کنید.", currentVersion = article.Version });
    article.Title = revision.Title; article.Summary = revision.Summary; article.Body = revision.Body;
    article.Category = revision.Category; article.Tags = revision.Tags;
    article.Version++; article.UpdatedByUserId = userId.Value; article.UpdatedAt = DateTimeOffset.UtcNow;
    db.KnowledgeArticleRevisions.Add(new KnowledgeArticleRevision
    {
        ArticleId = article.Id, Version = article.Version, Title = article.Title, Summary = article.Summary ?? "",
        Body = article.Body, Category = article.Category, Tags = article.Tags,
        ChangeNote = Clean(request.ChangeNote) ?? $"بازیابی از نسخه {revision.Version}", CreatedByUserId = userId.Value
    });
    RecordAuditEvent(db, organizationId, userId.Value, "knowledge.revision_restored", "KnowledgeArticle", article.Id, $"نسخه {revision.Version} مقاله «{article.Title}» بازیابی شد.");
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { article.Id, article.Version, article.Title, article.UpdatedAt });
});

var automationApi = app.MapGroup("/api/organizations/{organizationId:guid}/automations").RequireAuthorization();

automationApi.MapGet("/rules", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var rules = await db.AutomationRules.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .OrderByDescending(x => x.CreatedAt)
        .Select(x => new { x.Id, x.Name, x.Trigger, x.ConditionField, x.ConditionOperator, x.ConditionValue,
            x.ActionType, x.ActivityType, x.ActionValue, x.IsEnabled, x.CreatedAt, x.UpdatedAt,
            totalRuns = db.AutomationRuns.Count(r => r.RuleId == x.Id),
            successfulRuns = db.AutomationRuns.Count(r => r.RuleId == x.Id && r.Status == "Succeeded") })
        .ToListAsync(ct);
    return Results.Ok(rules);
});

automationApi.MapPost("/rules", async (Guid organizationId, AutomationRuleRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var name = request.Name?.Trim(); var trigger = request.Trigger?.Trim();
    var conditionField = request.ConditionField?.Trim() ?? "Any";
    var conditionOperator = request.ConditionOperator?.Trim() ?? "Equals";
    var actionType = request.ActionType?.Trim(); var actionValue = request.ActionValue?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
        trigger is not ("ContactCreated" or "LeadCreated" or "LeadStageChanged" or "ContentApproved" or "InboxMessageReceived") ||
        conditionField is not ("Any" or "LeadStage" or "LeadSource" or "ContactCompany" or "ContentChannel" or "ContentTitle" or "MessageBody") ||
        conditionOperator is not ("Equals" or "Contains") ||
        actionType is not ("CreateActivity" or "UpdateLeadStage") ||
        string.IsNullOrWhiteSpace(actionValue) || actionValue.Length > 1000 ||
        (request.ConditionValue?.Length ?? 0) > 240 ||
        (conditionField != "Any" && string.IsNullOrWhiteSpace(request.ConditionValue)) ||
        ((conditionField is "LeadStage" or "LeadSource") && trigger == "ContactCreated") ||
        (conditionField == "ContactCompany" && trigger != "ContactCreated") ||
        (conditionField == "MessageBody" && trigger != "InboxMessageReceived") ||
        (trigger == "InboxMessageReceived" && conditionField is not ("Any" or "MessageBody")) ||
        (actionType == "UpdateLeadStage" && (trigger == "ContactCreated" || trigger == "ContentApproved" || trigger == "InboxMessageReceived")) ||
        (trigger == "ContentApproved" && conditionField is not ("Any" or "ContentChannel" or "ContentTitle")) ||
        (trigger != "ContentApproved" && (conditionField is "ContentChannel" or "ContentTitle")) ||
        (actionType == "CreateActivity" && request.ActivityType is not ("Note" or "Call" or "Email" or "Meeting" or "Task")))
        return Results.BadRequest(new { error = "محرک، شرط یا اقدام قانون معتبر نیست." });
    if (actionType == "UpdateLeadStage" &&
        !await IsAllowedConfiguredStatus(db, organizationId, "crm.lead", actionValue, leadStages, ct))
        return Results.BadRequest(new { error = "مرحله مقصد باید یک وضعیت فعال فروش باشد." });
    var rule = new AutomationRule
    {
        OrganizationId = organizationId, CreatedByUserId = userId.Value, Name = name, Trigger = trigger,
        ConditionField = conditionField, ConditionOperator = conditionOperator,
        ConditionValue = conditionField == "Any" ? null : Clean(request.ConditionValue),
        ActionType = actionType, ActivityType = actionType == "CreateActivity" ? request.ActivityType : null,
        ActionValue = actionValue, IsEnabled = request.IsEnabled
    };
    db.AutomationRules.Add(rule);
    RecordAuditEvent(db, organizationId, userId.Value, "automation.created", "AutomationRule", rule.Id, $"قانون اتوماسیون «{rule.Name}» ایجاد شد.");
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/automations/rules/{rule.Id}",
        new { rule.Id, rule.Name, rule.Trigger, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue,
            rule.ActionType, rule.ActivityType, rule.ActionValue, rule.IsEnabled, rule.CreatedAt, totalRuns = 0, successfulRuns = 0 });
});

automationApi.MapPut("/rules/{ruleId:guid}", async (Guid organizationId, Guid ruleId, AutomationRuleRequest request, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.OrganizationId == organizationId, ct);
    if (rule is null) return Results.NotFound();
    var name = request.Name?.Trim(); var trigger = request.Trigger?.Trim();
    var conditionField = request.ConditionField?.Trim() ?? "Any";
    var conditionOperator = request.ConditionOperator?.Trim() ?? "Equals";
    var actionType = request.ActionType?.Trim(); var actionValue = request.ActionValue?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
        trigger is not ("ContactCreated" or "LeadCreated" or "LeadStageChanged" or "ContentApproved" or "InboxMessageReceived") ||
        conditionField is not ("Any" or "LeadStage" or "LeadSource" or "ContactCompany" or "ContentChannel" or "ContentTitle" or "MessageBody") ||
        conditionOperator is not ("Equals" or "Contains") ||
        actionType is not ("CreateActivity" or "UpdateLeadStage") ||
        string.IsNullOrWhiteSpace(actionValue) || actionValue.Length > 1000 ||
        (request.ConditionValue?.Length ?? 0) > 240 ||
        (conditionField != "Any" && string.IsNullOrWhiteSpace(request.ConditionValue)) ||
        ((conditionField is "LeadStage" or "LeadSource") && trigger == "ContactCreated") ||
        (conditionField == "ContactCompany" && trigger != "ContactCreated") ||
        (conditionField == "MessageBody" && trigger != "InboxMessageReceived") ||
        (trigger == "InboxMessageReceived" && conditionField is not ("Any" or "MessageBody")) ||
        (actionType == "UpdateLeadStage" && (trigger == "ContactCreated" || trigger == "ContentApproved" || trigger == "InboxMessageReceived")) ||
        (trigger == "ContentApproved" && conditionField is not ("Any" or "ContentChannel" or "ContentTitle")) ||
        (trigger != "ContentApproved" && (conditionField is "ContentChannel" or "ContentTitle")) ||
        (actionType == "CreateActivity" && request.ActivityType is not ("Note" or "Call" or "Email" or "Meeting" or "Task")))
        return Results.BadRequest(new { error = "محرک، شرط یا اقدام قانون معتبر نیست." });
    if (actionType == "UpdateLeadStage" &&
        !await IsAllowedConfiguredStatus(db, organizationId, "crm.lead", actionValue, leadStages, ct))
        return Results.BadRequest(new { error = "مرحله مقصد باید یک وضعیت فعال فروش باشد." });
    rule.Name = name; rule.Trigger = trigger; rule.ConditionField = conditionField;
    rule.ConditionOperator = conditionOperator; rule.ConditionValue = conditionField == "Any" ? null : Clean(request.ConditionValue);
    rule.ActionType = actionType; rule.ActivityType = actionType == "CreateActivity" ? request.ActivityType : null;
    rule.ActionValue = actionValue; rule.IsEnabled = request.IsEnabled; rule.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "automation.updated", "AutomationRule", rule.Id, $"قانون اتوماسیون «{rule.Name}» ویرایش شد.");
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { rule.Id, rule.Name, rule.Trigger, rule.ConditionField, rule.ConditionOperator, rule.ConditionValue,
        rule.ActionType, rule.ActivityType, rule.ActionValue, rule.IsEnabled, rule.UpdatedAt });
});

automationApi.MapDelete("/rules/{ruleId:guid}", async (Guid organizationId, Guid ruleId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();
    var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.OrganizationId == organizationId, ct);
    if (rule is null) return Results.NotFound();
    rule.IsEnabled = false;
    rule.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "automation.disabled", "AutomationRule", rule.Id, $"قانون اتوماسیون «{rule.Name}» غیرفعال شد.");
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

automationApi.MapPost("/rules/{ruleId:guid}/run-now", async (Guid organizationId, Guid ruleId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();

    var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.OrganizationId == organizationId, ct);
    if (rule is null) return Results.NotFound();
    if (!rule.IsEnabled) return Results.Conflict(new { error = "برای اجرای دستی، ابتدا قانون را فعال کنید." });

    string subjectType;
    Guid subjectId;
    object payload;
    if (rule.Trigger == "ContactCreated")
    {
        var contact = await db.CrmContacts.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (contact is null) return Results.Conflict(new { error = "برای اجرای این قانون، ابتدا یک مخاطب در CRM ثبت کنید." });
        subjectType = "Contact";
        subjectId = contact.Id;
        payload = new { manual = true, requestedBy = userId.Value, contactId = contact.Id };
    }
    else if (rule.Trigger == "ContentApproved")
    {
        var draft = await db.ContentDrafts.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status == "Approved")
            .OrderByDescending(x => x.ApprovedAt).FirstOrDefaultAsync(ct);
        if (draft is null) return Results.Conflict(new { error = "برای اجرای این قانون، ابتدا یک محتوا را تا مرحله تأیید نهایی تأیید کنید." });
        subjectType = "ContentDraft";
        subjectId = draft.Id;
        payload = new { manual = true, requestedBy = userId.Value, contentTitle = draft.Title, contentChannel = draft.Channel };
    }
    else
    {
        var lead = await db.CrmLeads.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        if (lead is null) return Results.Conflict(new { error = "برای اجرای این قانون، ابتدا یک سرنخ فروش در CRM ثبت کنید." });
        subjectType = "Lead";
        subjectId = lead.Id;
        payload = new { manual = true, requestedBy = userId.Value, leadId = lead.Id };
    }

    var run = new AutomationRun
    {
        OrganizationId = organizationId, RuleId = rule.Id, Trigger = rule.Trigger,
        SubjectType = subjectType, SubjectId = subjectId,
        PayloadJson = JsonSerializer.Serialize(payload), Status = "Pending"
    };
    db.AutomationRuns.Add(run);
    RecordAuditEvent(db, organizationId, userId.Value, "automation.run_requested", "AutomationRun", run.Id,
        $"اجرای دستی قانون «{rule.Name}» برای آخرین رکورد مرتبط به صف افزوده شد.",
        new { ruleId = rule.Id, subjectType, subjectId });
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/organizations/{organizationId}/automations/runs", new
    {
        run.Id, run.RuleId, run.Trigger, run.SubjectType, run.SubjectId, run.Status, run.CreatedAt,
        message = "اجرا به صف پس‌زمینه افزوده شد؛ نتیجه در تاریخچه اجرا قابل مشاهده است."
    });
});

automationApi.MapGet("/runs", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, int? take, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var limit = Math.Clamp(take ?? 50, 1, 200);
    var runs = await db.AutomationRuns.AsNoTracking().Where(x => x.OrganizationId == organizationId)
        .OrderByDescending(x => x.CreatedAt).Take(limit)
        .Join(db.AutomationRules.AsNoTracking(), run => run.RuleId, rule => rule.Id,
            (run, rule) => new { run.Id, run.RuleId, ruleName = rule.Name, run.Trigger, run.SubjectType,
                run.SubjectId, run.Status, run.Result, run.Error, run.AttemptCount, maxAttempts = 3, run.CreatedAt, run.StartedAt, run.CompletedAt })
        .ToListAsync(ct);
    return Results.Ok(runs);
});

automationApi.MapPost("/runs/{runId:guid}/retry", async (Guid organizationId, Guid runId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null) return Results.Unauthorized();
    var role = await db.OrganizationMemberships.Where(x => x.UserId == userId && x.OrganizationId == organizationId)
        .Select(x => x.Role).SingleOrDefaultAsync(ct);
    if (role is null) return Results.NotFound();
    if (role is not ("Owner" or "Admin")) return Results.Forbid();

    var run = await db.AutomationRuns.SingleOrDefaultAsync(x => x.Id == runId && x.OrganizationId == organizationId, ct);
    if (run is null) return Results.NotFound();
    if (run.Status != "Failed")
        return Results.Conflict(new { error = "فقط اجرای ناموفق را می‌توان دوباره به صف فرستاد." });
    if (run.AttemptCount >= 3)
        return Results.Conflict(new { error = "سقف سه تلاش برای این اجرا تکمیل شده است؛ برای جلوگیری از حلقه تکرار، اجرای مجدد مجاز نیست." });
    var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == run.RuleId && x.OrganizationId == organizationId, ct);
    if (rule is null || !rule.IsEnabled)
        return Results.Conflict(new { error = "قانون حذف یا غیرفعال است؛ ابتدا آن را فعال کنید." });

    run.Status = "Pending";
    run.Error = null;
    run.Result = null;
    run.StartedAt = null;
    run.CompletedAt = null;
    run.CreatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "automation.run_retried", "AutomationRun", run.Id,
        $"اجرای گردش‌کار «{rule.Name}» برای تلاش بعدی به صف بازگردانده شد.", new { attemptCount = run.AttemptCount, maxAttempts = 3 });
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/organizations/{organizationId}/automations/runs", new { run.Id, run.Status, run.AttemptCount, maxAttempts = 3 });
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

growthAgentApi.MapGet("/", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IDataProtectionProvider protection, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var org = await db.Organizations.AsNoTracking().Where(x => x.Id == organizationId)
        .Select(x => new { x.GrowthAgentEnabled, x.GrowthAgentLastRunAt }).SingleOrDefaultAsync(ct);
    if (org is null) return Results.NotFound();
    var aiProvider = await ResolveAiProviderConfigAsync(db, protection, config, organizationId, ct);
    var aiConfigured = aiProvider.Enabled && !string.IsNullOrWhiteSpace(aiProvider.ApiKey) && !string.IsNullOrWhiteSpace(aiProvider.Model) && Uri.TryCreate(aiProvider.BaseUrl, UriKind.Absolute, out var aiEndpoint) && (aiEndpoint.Scheme == Uri.UriSchemeHttps || aiEndpoint.IsLoopback);
    return Results.Ok(new {
        enabled = org.GrowthAgentEnabled,
        lastRunAt = org.GrowthAgentLastRunAt,
        aiConfigured,
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


contentApi.MapGet("/ai/status", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    IDataProtectionProvider protection, IConfiguration config, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var provider = await ResolveAiProviderConfigAsync(db, protection, config, organizationId, ct);
    var endpointValid = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var endpoint) &&
        (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.IsLoopback) && string.IsNullOrEmpty(endpoint.UserInfo);
    var keyStored = !string.IsNullOrWhiteSpace(provider.ApiKey);
    return Results.Ok(new
    {
        configured = provider.Enabled && keyStored && endpointValid && !string.IsNullOrWhiteSpace(provider.Model),
        model = string.IsNullOrWhiteSpace(provider.Model) ? "not-configured" : provider.Model,
        provider = "OpenAI-compatible", keyStored, endpointValid, enabled = provider.Enabled,
        source = provider.OrganizationConfigured ? "organization" : "server-environment"
    });
});


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
    if (!string.IsNullOrWhiteSpace(status) && status != "All") query = query.Where(x => x.Status.ToLower() == status.ToLower());
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

contentApi.MapPost("/drafts", async (Guid organizationId, CreateManualContentDraftRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var title = request.Title?.Trim();
    var content = request.Content?.Trim();
    var channel = string.IsNullOrWhiteSpace(request.Channel) ? "General" : request.Channel.Trim();
    var tone = string.IsNullOrWhiteSpace(request.Tone) ? "Professional" : request.Tone.Trim();
    if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || string.IsNullOrWhiteSpace(content) ||
        content.Length > 30000 || channel.Length > 40 || tone.Length > 40)
        return Results.BadRequest(new { error = "عنوان، متن، کانال یا لحن معتبر نیست." });
    if (request.BrandId is Guid brandId &&
        !await db.Brands.AnyAsync(x => x.Id == brandId && x.OrganizationId == organizationId, ct))
        return Results.BadRequest(new { error = "برند انتخاب‌شده متعلق به این سازمان نیست." });
    var draft = new ContentDraft
    {
        OrganizationId = organizationId, BrandId = request.BrandId, CreatedByUserId = userId.Value,
        Title = title, Content = content, Channel = channel, Tone = tone,
        Prompt = "ایجاد دستی محتوا"
    };
    draft.Versions.Add(new ContentDraftVersion
    {
        DraftId = draft.Id, VersionNumber = 1, Title = draft.Title, Content = draft.Content,
        ChangeNote = "نسخه اولیه ایجاد دستی", CreatedByUserId = userId.Value
    });
    db.ContentDrafts.Add(draft);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/content/drafts/{draft.Id}",
        new { draft.Id, draft.Title, draft.Content, draft.Channel, draft.Tone, draft.Status, draft.CreatedAt, versionCount = 1 });
});

contentApi.MapPost("/generate", async (Guid organizationId, GenerateContentRequest request,
    ClaimsPrincipal principal, MajaziDbContext db, IConfiguration config, IDataProtectionProvider protection,
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
    var provider = await ResolveAiProviderConfigAsync(db, protection, config, organizationId, ct);
    var apiKey = provider.ApiKey;
    var baseUrl = provider.BaseUrl?.TrimEnd('/');
    var model = provider.Model;
    if (!provider.Enabled || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl) ||
        !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
        return Results.Problem("اتصال هوش مصنوعی برای این سازمان پیکربندی یا فعال نشده است.", statusCode: 503);
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
    if (stage == 2)
    {
        draft.ApprovedAt = DateTimeOffset.UtcNow;
        draft.ApprovedByUserId = userId.Value;
        await QueueAutomationRunsAsync(db, organizationId, "ContentApproved", "ContentDraft", draft.Id,
            new { contentTitle = draft.Title, contentChannel = draft.Channel }, ct);
    }
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

inboxApi.MapGet("/assignees", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var members = await db.OrganizationMemberships.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.User != null && x.User.IsActive)
        .OrderBy(x => x.User!.Email)
        .Select(x => new { id = x.UserId, email = x.User!.Email, role = x.Role })
        .ToListAsync(ct);
    return Results.Ok(members);
});

inboxApi.MapGet("/conversations", async (Guid organizationId, ClaimsPrincipal principal, MajaziDbContext db,
    string? status, string? q, string? assignment, int? page, int? pageSize, CancellationToken ct) =>
{
    var userId = CurrentUserId(principal);
    if (userId is null || !await IsMember(db, userId.Value, organizationId, ct)) return Results.NotFound();
    var query = db.InboxConversations.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(assignment))
    {
        if (assignment.Equals("me", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.AssignedToUserId == userId.Value);
        else if (assignment.Equals("unassigned", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.AssignedToUserId == null);
        else if (Guid.TryParse(assignment, out var assigneeId))
        {
            if (!await IsMember(db, assigneeId, organizationId, ct)) return Results.BadRequest(new { error = "عضو انتخاب‌شده متعلق به این سازمان نیست." });
            query = query.Where(x => x.AssignedToUserId == assigneeId);
        }
        else return Results.BadRequest(new { error = "فیلتر مسئول مکالمه معتبر نیست." });
    }

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
    var initialStatus = await db.StatusDefinitions.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.Module == "inbox.conversation" && x.IsActive)
        .OrderBy(x => x.SortOrder).Select(x => x.Key).FirstOrDefaultAsync(ct);
    if (initialStatus is null)
    {
        var hasDefinitions = await db.StatusDefinitions.AnyAsync(
            x => x.OrganizationId == organizationId && x.Module == "inbox.conversation", ct);
        if (hasDefinitions)
            return Results.Conflict(new { error = "همه وضعیت‌های مکالمه غیرفعال‌اند؛ ابتدا یک وضعیت را در تنظیمات فعال کنید." });
        var now = DateTimeOffset.UtcNow;
        var defaults = defaultStatusesByModule["inbox.conversation"];
        db.StatusDefinitions.AddRange(defaults.Select((x, index) => new StatusDefinition
        {
            OrganizationId = organizationId, Module = "inbox.conversation", Key = x.Key, Label = x.Label,
            Color = x.Color, SortOrder = (index + 1) * 10, IsActive = true, CreatedAt = now, UpdatedAt = now
        }));
        initialStatus = defaults[0].Key;
    }
    var item = new InboxConversation
    {
        OrganizationId = organizationId, ChannelConnectionId = request.ChannelConnectionId,
        ContactId = request.ContactId, Subject = subject, ExternalConversationId = externalId,
        Status = initialStatus, LastMessageAt = DateTimeOffset.UtcNow
    };
    db.InboxConversations.Add(item);
    RecordAuditEvent(db, organizationId, userId.Value, "inbox.conversation_created", "InboxConversation", item.Id, "مکالمه جدید ایجاد شد.");
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{organizationId}/inbox/conversations/{item.Id}",
        new { item.Id, item.Subject, item.Status, item.ChannelConnectionId, item.ContactId, item.CreatedAt });
});

inboxApi.MapPost("/conversations/{conversationId:guid}/messages", async (Guid organizationId, Guid conversationId,
    CreateInboxMessageRequest request, ClaimsPrincipal principal, MajaziDbContext db,
    IDataProtectionProvider protection, IHttpClientFactory httpClientFactory, IConfiguration config, CancellationToken ct) =>
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

    // WhatsApp Cloud API delivery.
    if (conversation.ChannelConnection?.Provider == "WhatsApp")
    {
        var channel = conversation.ChannelConnection;
        if (channel.Status != "Connected" || string.IsNullOrWhiteSpace(channel.EncryptedAccessToken) || string.IsNullOrWhiteSpace(channel.ExternalAccountId))
            return Results.Conflict(new { error = "اتصال WhatsApp باید با Phone Number ID و توکن معتبر تأیید شده باشد." });
        if (string.IsNullOrWhiteSpace(conversation.ExternalConversationId) || conversation.ExternalConversationId.Length > 32 ||
            !conversation.ExternalConversationId.All(ch => char.IsAsciiDigit(ch) || ch == '+'))
            return Results.Conflict(new { error = "شناسه گیرنده WhatsApp معتبر نیست؛ مکالمه باید از webhook رسمی دریافت شده باشد." });
        string tokenValue;
        try { tokenValue = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(channel.EncryptedAccessToken); }
        catch (CryptographicException) { return Results.Conflict(new { error = "توکن WhatsApp قابل رمزگشایی نیست؛ اتصال را دوباره ثبت کنید." }); }
        var version = config["Meta:GraphApiVersion"] ?? "v22.0";
        try
        {
            using var outbound = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(channel.ExternalAccountId)}/messages");
            outbound.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            outbound.Content = JsonContent.Create(new { messaging_product = "whatsapp", recipient_type = "individual",
                to = conversation.ExternalConversationId.TrimStart('+'), type = "text", text = new { preview_url = false, body } });
            using var response = await httpClientFactory.CreateClient("MetaOAuth").SendAsync(outbound, ct);
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem("WhatsApp Cloud API ارسال پیام را نپذیرفت؛ تنظیمات شماره و پنجره مجاز پیام‌رسانی را بررسی کنید.", statusCode: 502);
            if (payload.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0 && messages[0].TryGetProperty("id", out var waMessageId))
                message.ExternalMessageId = waMessageId.GetString();
            message.Status = "Sent"; message.SentAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.Problem("ارسال WhatsApp timeout شد؛ وضعیت تحویل را در Meta بررسی کنید.", statusCode: 504); }
        catch (HttpRequestException) { return Results.Problem("ارتباط با WhatsApp Cloud API برقرار نشد.", statusCode: 502); }
    }
    // Instagram Messaging and Facebook Messenger Send API.
    else if (conversation.ChannelConnection?.Provider is "Instagram" or "Facebook")
    {
        var channel = conversation.ChannelConnection;
        if (channel.Status != "Connected" || string.IsNullOrWhiteSpace(channel.EncryptedAccessToken) ||
            string.IsNullOrWhiteSpace(channel.ExternalAccountId))
            return Results.Conflict(new { error = "اتصال Meta باید با شناسه حساب و توکن معتبر تأیید شده باشد." });
        var recipientId = conversation.ExternalConversationId;
        if (string.IsNullOrWhiteSpace(recipientId) || recipientId.Length > 180 ||
            recipientId.Any(char.IsWhiteSpace))
            return Results.Conflict(new { error = "شناسه گیرنده معتبر نیست؛ مکالمه باید از وب‌هوک رسمی Meta دریافت شده باشد." });
        string tokenValue;
        try { tokenValue = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(channel.EncryptedAccessToken); }
        catch (CryptographicException) { return Results.Conflict(new { error = "توکن Meta قابل رمزگشایی نیست؛ اتصال را دوباره برقرار کنید." }); }
        var version = config["Meta:GraphApiVersion"] ?? "v22.0";
        var host = channel.Provider == "Instagram" ? "graph.instagram.com" : "graph.facebook.com";
        try
        {
            using var outbound = new HttpRequestMessage(HttpMethod.Post,
                $"https://{host}/{version}/{Uri.EscapeDataString(channel.ExternalAccountId)}/messages");
            outbound.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            outbound.Content = JsonContent.Create(new { recipient = new { id = recipientId }, message = new { text = body } });
            using var response = await httpClientFactory.CreateClient("MetaOAuth").SendAsync(outbound, ct);
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(channel.Provider == "Instagram"
                    ? "Instagram Messaging API پیام را نپذیرفت؛ مجوز instagram_business_manage_messages و پنجره مجاز پاسخ را بررسی کنید."
                    : "Messenger Send API پیام را نپذیرفت؛ مجوز pages_messaging و پنجره مجاز پاسخ را بررسی کنید.", statusCode: 502);
            if (payload.RootElement.TryGetProperty("message_id", out var messageId))
                message.ExternalMessageId = messageId.GetString();
            else if (payload.RootElement.TryGetProperty("id", out var id))
                message.ExternalMessageId = id.GetString();
            message.Status = "Sent";
            message.SentAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Results.Problem("ارسال پیام از طریق Meta در زمان مقرر پاسخ نداد.", statusCode: 504); }
        catch (HttpRequestException)
        { return Results.Problem("ارتباط با Meta Messaging API برقرار نشد؛ پیام در سامانه ثبت نشد.", statusCode: 502); }
    }
    // Official Iranian bot APIs: Bale and Rubika.
    else if (conversation.ChannelConnection?.Provider is "Bale" or "Rubika")
    {
        var channel = conversation.ChannelConnection;
        if (channel.Status != "Connected" || string.IsNullOrWhiteSpace(channel.EncryptedAccessToken))
            return Results.Conflict(new { error = $"ربات {channel.Provider} متصل و تأییدشده نیست." });
        if (string.IsNullOrWhiteSpace(conversation.ExternalConversationId) || conversation.ExternalConversationId.Length > 180)
            return Results.Conflict(new { error = "شناسه گفتگوی پیام‌رسان معتبر نیست؛ مکالمه باید از وب‌هوک رسمی دریافت شده باشد." });
        string iranianBotToken;
        try { iranianBotToken = protection.CreateProtector("Majazi.SocialConnections.v1").Unprotect(channel.EncryptedAccessToken); }
        catch (CryptographicException) { return Results.Conflict(new { error = "توکن ربات قابل رمزگشایی نیست؛ اتصال را دوباره ثبت کنید." }); }
        var endpoint = channel.Provider == "Bale"
            ? $"https://tapi.bale.ai/bot{iranianBotToken}/sendMessage"
            : $"https://botapi.rubika.ir/v3/{iranianBotToken}/sendMessage";
        try
        {
            using var response = await httpClientFactory.CreateClient("TelegramBotApi").PostAsJsonAsync(endpoint,
                new { chat_id = conversation.ExternalConversationId, text = body }, ct);
            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = payload.RootElement;
            var ok = channel.Provider == "Bale"
                ? response.IsSuccessStatusCode && root.TryGetProperty("ok", out var baleOk) && baleOk.ValueKind == JsonValueKind.True
                : response.IsSuccessStatusCode && root.TryGetProperty("status", out var rubikaStatus) && rubikaStatus.GetString() == "OK";
            if (!ok) return Results.Problem($"پیام‌رسان {channel.Provider} ارسال پیام را نپذیرفت؛ شناسه چت و دسترسی ربات را بررسی کنید.", statusCode: 502);
            var result = root.TryGetProperty("result", out var baleResult) ? baleResult :
                root.TryGetProperty("data", out var rubikaData) ? rubikaData : root;
            if (result.TryGetProperty("message_id", out var messageId)) message.ExternalMessageId = messageId.ToString();
            else if (result.TryGetProperty("message", out var messageNode) && messageNode.TryGetProperty("message_id", out var nestedId))
                message.ExternalMessageId = nestedId.ToString();
            message.Status = "Sent"; message.SentAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Results.Problem($"ارسال پیام به {channel.Provider} timeout شد.", statusCode: 504); }
        catch (HttpRequestException)
        { return Results.Problem($"ارتباط با API {channel.Provider} برقرار نشد.", statusCode: 502); }
    }
    // Telegram replies are delivered synchronously through the official Bot API.
    // Providers without a delivery adapter remain explicitly queued.
    else if (conversation.ChannelConnection?.Provider == "Telegram")
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
                ? $"پیام با موفقیت از طریق {conversation.ChannelConnection?.Provider} API ارسال شد."
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
        var nextStatus = request.Status.Trim().ToLowerInvariant();
        if (!await IsAllowedConfiguredStatus(db, organizationId, "inbox.conversation", nextStatus,
            new HashSet<string>(["open", "pending", "resolved"]), ct))
            return Results.BadRequest(new { error = "وضعیت مکالمه معتبر یا فعال نیست." });
        item.Status = nextStatus;
    }
    if (request.AssignedToUserId.HasValue)
    {
        var assignee = request.AssignedToUserId.Value;
        if (assignee != Guid.Empty && !await IsMember(db, assignee, organizationId, ct))
            return Results.BadRequest(new { error = "مسئول انتخاب‌شده عضو این سازمان نیست." });
        item.AssignedToUserId = assignee == Guid.Empty ? null : assignee;
    }
    item.UpdatedAt = DateTimeOffset.UtcNow;
    RecordAuditEvent(db, organizationId, userId.Value, "inbox.conversation_updated", "InboxConversation", item.Id, "وضعیت یا مسئول مکالمه تغییر کرد.", new { item.Status, item.AssignedToUserId });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { item.Id, item.Status, item.AssignedToUserId, item.UpdatedAt });
});

app.Run();



static async Task<AiProviderConfig> ResolveAiProviderConfigAsync(MajaziDbContext db, IDataProtectionProvider protection,
    IConfiguration config, Guid organizationId, CancellationToken ct)
{
    var saved = await db.OrganizationAiProviderSettings.AsNoTracking()
        .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
    if (saved is null)
        return new AiProviderConfig(config["AI:ApiKey"], config["AI:BaseUrl"], config["AI:Model"], true, false);
    string? key = config["AI:ApiKey"];
    if (!string.IsNullOrWhiteSpace(saved.EncryptedApiKey))
    {
        try { key = protection.CreateProtector("Majazi.AIProviderSettings.v1", organizationId.ToString("N")).Unprotect(saved.EncryptedApiKey); }
        catch (CryptographicException) { key = config["AI:ApiKey"]; }
    }
    return new AiProviderConfig(key, saved.BaseUrl, saved.Model, saved.IsEnabled, true);
}

static void RecordAuditEvent(MajaziDbContext db, Guid organizationId, Guid? actorUserId,
    string action, string entityType, Guid? entityId, string summary, object? metadata = null)
{
    db.OrganizationAuditEvents.Add(new OrganizationAuditEvent
    {
        OrganizationId = organizationId, ActorUserId = actorUserId, Action = action,
        EntityType = entityType, EntityId = entityId,
        Summary = summary.Length > 500 ? summary[..500] : summary,
        MetadataJson = JsonSerializer.Serialize(metadata ?? new { })
    });
}

static async Task QueueAutomationRunsAsync(MajaziDbContext db, Guid organizationId, string trigger, string subjectType, Guid subjectId, object payload, CancellationToken ct)
{
    var rules = await db.AutomationRules.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.Trigger == trigger && x.IsEnabled)
        .Select(x => x.Id).ToListAsync(ct);
    if (rules.Count == 0) return;
    var payloadJson = JsonSerializer.Serialize(payload);
    var eventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{trigger}|{subjectType}|{subjectId:D}|{payloadJson}")));
    var existing = await db.AutomationRuns.AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.EventKey == eventKey)
        .Select(x => x.RuleId).ToListAsync(ct);
    var existingRuleIds = existing.ToHashSet();
    db.AutomationRuns.AddRange(rules.Where(ruleId => !existingRuleIds.Contains(ruleId)).Select(ruleId => new AutomationRun
    {
        OrganizationId = organizationId, RuleId = ruleId, Trigger = trigger,
        SubjectType = subjectType, SubjectId = subjectId, PayloadJson = payloadJson,
        EventKey = eventKey, Status = "Pending"
    }));
}

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

static async Task<bool> IsAllowedConfiguredStatus(MajaziDbContext db, Guid organizationId, string module, string status, HashSet<string> fallback, CancellationToken ct)
{
    var definitions = db.StatusDefinitions.Where(x => x.OrganizationId == organizationId && x.Module == module);
    if (!await definitions.AnyAsync(ct)) return fallback.Contains(status, StringComparer.OrdinalIgnoreCase);
    return await definitions.AnyAsync(x => x.IsActive && x.Key.ToLower() == status.ToLower(), ct);
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

static string BotWebhookSignature(string signingKey, Guid connectionId, string provider) =>
    Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes($"majazi:{provider}:{connectionId:D}"))).ToLowerInvariant();

static async Task<IResult> ProcessIranianBotWebhookAsync(string provider, Guid connectionId, string signature,
    HttpRequest request, MajaziDbContext db, IConfiguration config, CancellationToken ct)
{
    var signingKey = config["Jwt:SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey)) return Results.Problem(statusCode: 503);
    var expected = BotWebhookSignature(signingKey, connectionId, provider);
    var suppliedBytes = Encoding.UTF8.GetBytes(signature);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
        return Results.Unauthorized();
    using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var root = doc.RootElement;
    JsonElement message;
    string? chatId = null, externalId = null, body = null, senderName = null;
    DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
    if (provider == "Bale")
    {
        if (!root.TryGetProperty("message", out message) && !root.TryGetProperty("edited_message", out message))
            return Results.Ok(new { accepted = true, ignored = true });
        if (!message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatValue) ||
            !message.TryGetProperty("message_id", out var messageValue)) return Results.BadRequest();
        chatId = chatValue.ToString(); externalId = messageValue.ToString();
        body = message.TryGetProperty("text", out var textValue) ? textValue.GetString() :
            message.TryGetProperty("caption", out var caption) ? caption.GetString() : null;
        senderName = message.TryGetProperty("from", out var from)
            ? string.Join(" ", new[] { from.TryGetProperty("first_name", out var first) ? first.GetString() : null,
                from.TryGetProperty("last_name", out var last) ? last.GetString() : null }.Where(x => !string.IsNullOrWhiteSpace(x))) : null;
        if (message.TryGetProperty("date", out var date) && date.TryGetInt64(out var unix)) receivedAt = DateTimeOffset.FromUnixTimeSeconds(unix);
    }
    else
    {
        if (!root.TryGetProperty("update", out var update) ||
            !update.TryGetProperty("new_message", out message)) return Results.Ok(new { accepted = true, ignored = true });
        chatId = update.TryGetProperty("chat_id", out var chat) ? chat.ToString() : null;
        externalId = message.TryGetProperty("message_id", out var mid) ? mid.ToString() : null;
        body = message.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
        senderName = message.TryGetProperty("sender_id", out var sender) ? sender.ToString() : null;
        if (message.TryGetProperty("time", out var time) && long.TryParse(time.ToString(), out var unix))
        { try { receivedAt = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { } }
    }
    if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(body))
        return Results.Ok(new { accepted = true, ignored = true });
    if (body.Length > 10000) body = body[..10000];
    var channel = await db.SocialChannelConnections.SingleOrDefaultAsync(
        x => x.Id == connectionId && x.Provider == provider && x.Status == "Connected", ct);
    if (channel is null) return Results.NotFound();
    var conversation = await db.InboxConversations.SingleOrDefaultAsync(
        x => x.ChannelConnectionId == connectionId && x.ExternalConversationId == chatId, ct);
    if (conversation is null)
    {
        conversation = new InboxConversation { OrganizationId = channel.OrganizationId, ChannelConnectionId = channel.Id,
            ExternalConversationId = chatId, Subject = string.IsNullOrWhiteSpace(senderName) ? $"{provider} {chatId}" : senderName,
            Status = "Open" };
        db.InboxConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
    }
    if (await db.InboxMessages.AnyAsync(x => x.ConversationId == conversation.Id && x.ExternalMessageId == externalId, ct))
        return Results.Ok(new { accepted = true, duplicate = true });
    var inboxMessage = new InboxMessage { ConversationId = conversation.Id, ExternalMessageId = externalId,
        Direction = "Inbound", Body = body, Status = "Received", CreatedAt = receivedAt, ProviderPayload = JsonSerializer.Serialize(root) };
    db.InboxMessages.Add(inboxMessage);
    await QueueAutomationRunsAsync(db, channel.OrganizationId, "InboxMessageReceived", "InboxMessage", inboxMessage.Id,
        new { messageBody = body, conversationSubject = conversation.Subject, channel = provider }, ct);
    conversation.LastMessageAt = receivedAt; conversation.UpdatedAt = DateTimeOffset.UtcNow;
    if (conversation.Status.Equals("resolved", StringComparison.OrdinalIgnoreCase)) conversation.Status = "open";
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { accepted = true });
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
public sealed record StatusDefinitionRequest(string? Module, string? Key, string? Label, string? Color, string? Description, int SortOrder, bool IsActive = true);
public sealed record UpdateStatusDefinitionRequest(string? Label, string? Color, string? Description, int SortOrder, bool IsActive);
public sealed record CreateBrandRequest(string? Name, string? Slug);
public sealed record AddMemberRequest(string? Email, string? Role);
public sealed record UpdateMemberRoleRequest(string? Role);
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

public sealed record CreateManualContentDraftRequest(Guid? BrandId, string? Title, string? Content, string? Channel, string? Tone);
public sealed record GenerateContentRequest(Guid? BrandId, string? Topic, string? Channel, string? Tone, string? Instructions);
public sealed record GenerateAssistantTextRequest(string? Module, string? Field, string? Context, string? CurrentText, string? Instructions);
public sealed record TextTemplateRequest(string? Module, string? Field, string? Name, string? Content, int SortOrder, bool IsActive = true);
public sealed record UpdateTextTemplateRequest(string? Name, string? Content, int SortOrder, bool IsActive);
public sealed record EditContentDraftRequest(string? Title, string? Content, string? ChangeNote);

public sealed record ScheduleContentRequest(Guid ConnectionId, string? TargetChatId, DateTimeOffset? ScheduledAt);
public sealed record PublishNowRequest(Guid ConnectionId, string? TargetChatId);


public sealed record ContentApprovalRequest(string? Comment);

public sealed record GrowthAgentSettingsRequest(bool Enabled);
public sealed record AutomationRuleRequest(string? Name, string? Trigger, string? ConditionField, string? ConditionOperator, string? ConditionValue, string? ActionType, string? ActivityType, string? ActionValue, bool IsEnabled = true);
public sealed record KnowledgeArticleRequest(string? Title, string? Slug, string? Summary, string? Body, string? Category, string? Tags, int Version = 0, string? ChangeNote = null);
public sealed record KnowledgeArticleStatusRequest(string? Status);
public sealed record KnowledgeRestoreRequest(int Version, string? ChangeNote);
public sealed record OrganizationAiProviderSettingsRequest(string? BaseUrl, string? Model, string? ApiKey, bool ClearApiKey, bool IsEnabled);
public sealed record SalesOrderLineRequest(string? Name, string? Sku, decimal Quantity, decimal UnitPrice, decimal Discount);
public sealed record SalesOrderRequest(Guid? ContactId, string? Currency, decimal Discount, decimal Tax, string? Notes, List<SalesOrderLineRequest>? Lines, int Version = 0);
public sealed record SalesOrderStatusRequest(string? Status);
public sealed record PaymentTransactionRequest(decimal Amount, string? Method, string? Status, string? Reference, string? Note);
public sealed record PaymentStatusUpdateRequest(string? Status);
public sealed record AiProviderConfig(string? ApiKey, string? BaseUrl, string? Model, bool Enabled, bool OrganizationConfigured);
