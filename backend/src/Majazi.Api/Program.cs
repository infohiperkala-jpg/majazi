using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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

app.MapPost("/api/auth/register", async (RegisterRequest request, MajaziDbContext db,
    IPasswordHasher<AppUser> hasher, IConfiguration config, CancellationToken ct) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    var password = request.Password;
    var orgName = request.OrganizationName?.Trim();
    var orgSlug = request.OrganizationSlug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || !email.Contains('@') ||
        string.IsNullOrWhiteSpace(password) || password.Length < 12 ||
        password.Length > 256 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) ||
        string.IsNullOrWhiteSpace(orgName) || orgName.Length > 180 ||
        string.IsNullOrWhiteSpace(orgSlug) || orgSlug.Length > 100)
        return Results.BadRequest(new { error = "اطلاعات نامعتبر است. گذرواژه باید حداقل ۱۲ نویسه و شامل حروف کوچک، بزرگ و عدد باشد." });

    if (await db.Users.AnyAsync(x => x.Email == email, ct))
        return Results.Conflict(new { error = "ثبت‌نام با این ایمیل امکان‌پذیر نیست." });
    if (await db.Organizations.AnyAsync(x => x.Slug == orgSlug, ct))
        return Results.Conflict(new { error = "شناسه سازمان قبلاً استفاده شده است." });

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
    IPasswordHasher<AppUser> hasher, IConfiguration config, CancellationToken ct) =>
{
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
        .Select(x => new { x.Id, x.ContactId, x.LeadId, x.ActivityType, x.Summary, x.OccurredAt, x.CreatedByUserId }).ToListAsync(ct);
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

app.Run();

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

static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

public sealed record RegisterRequest(string? Email, string? Password, string? OrganizationName, string? OrganizationSlug);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record CreateOrganizationRequest(string? Name, string? Slug);
public sealed record CreateBrandRequest(string? Name, string? Slug);
public sealed record AddMemberRequest(string? Email, string? Role);
public sealed record CrmContactRequest(Guid? BrandId, string? FullName, string? Email, string? Phone, string? Company, string? Notes);
public sealed record CrmLeadRequest(Guid? BrandId, Guid? ContactId, string? Title, string? Stage, decimal? EstimatedValue, string? Source, string? Notes);
public sealed record UpdateLeadStageRequest(string? Stage);
public sealed record CrmActivityRequest(Guid? ContactId, Guid? LeadId, string? ActivityType, string? Summary, DateTimeOffset? OccurredAt);
