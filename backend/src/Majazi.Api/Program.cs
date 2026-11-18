using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddDbContext<MajaziDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"])
        .AllowAnyHeader()
        .AllowAnyMethod()));
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseCors();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MajaziDbContext>();
    await db.Database.MigrateAsync();
}

app.MapHealthChecks("/health");
app.MapGet("/api", () => Results.Ok(new { name = "MAJAZI API", version = "0.2.0", status = "running" }));

app.MapGet("/api/organizations", async (MajaziDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Organizations.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name, x.Slug, x.CreatedAt }).ToListAsync(ct)));

app.MapPost("/api/organizations", async (CreateOrganizationRequest request, MajaziDbContext db, CancellationToken ct) =>
{
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
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/organizations/{item.Id}", new { item.Id, item.Name, item.Slug, item.CreatedAt });
});

app.MapPut("/api/organizations/{id:guid}", async (Guid id, CreateOrganizationRequest request, MajaziDbContext db, CancellationToken ct) =>
{
    var item = await db.Organizations.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound(new { error = "سازمان پیدا نشد." });
    var name = request.Name?.Trim();
    var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug))
        return Results.BadRequest(new { error = "نام و شناسه سازمان الزامی است." });
    if (name.Length > 180 || slug.Length > 100)
        return Results.BadRequest(new { error = "طول نام یا شناسه سازمان بیش از حد مجاز است." });
    if (await db.Organizations.AnyAsync(x => x.Id != id && x.Slug == slug, ct))
        return Results.Conflict(new { error = "این شناسه سازمان قبلاً ثبت شده است." });
    item.Name = name;
    item.Slug = slug;
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "شناسه سازمان تکراری است." }); }
    return Results.Ok(new { item.Id, item.Name, item.Slug, item.CreatedAt });
});

app.MapDelete("/api/organizations/{id:guid}", async (Guid id, MajaziDbContext db, CancellationToken ct) =>
{
    var item = await db.Organizations.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound(new { error = "سازمان پیدا نشد." });
    db.Organizations.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapGet("/api/organizations/{id:guid}/brands", async (Guid id, MajaziDbContext db, CancellationToken ct) =>
{
    if (!await db.Organizations.AnyAsync(x => x.Id == id, ct))
        return Results.NotFound(new { error = "سازمان پیدا نشد." });
    return Results.Ok(await db.Brands.AsNoTracking().Where(x => x.OrganizationId == id).OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.OrganizationId, x.Name, x.Slug, x.CreatedAt }).ToListAsync(ct));
});

app.MapPost("/api/organizations/{id:guid}/brands", async (Guid id, CreateBrandRequest request, MajaziDbContext db, CancellationToken ct) =>
{
    if (!await db.Organizations.AnyAsync(x => x.Id == id, ct))
        return Results.NotFound(new { error = "سازمان پیدا نشد." });
    var name = request.Name?.Trim();
    var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug))
        return Results.BadRequest(new { error = "نام و شناسه برند الزامی است." });
    if (name.Length > 180 || slug.Length > 100)
        return Results.BadRequest(new { error = "طول نام یا شناسه برند بیش از حد مجاز است." });
    if (await db.Brands.AnyAsync(x => x.OrganizationId == id && x.Slug == slug, ct))
        return Results.Conflict(new { error = "این شناسه برند قبلاً ثبت شده است." });

    var item = new Brand { OrganizationId = id, Name = name, Slug = slug };
    db.Brands.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/brands/{item.Id}", new { item.Id, item.OrganizationId, item.Name, item.Slug, item.CreatedAt });
});

app.MapPut("/api/brands/{id:guid}", async (Guid id, CreateBrandRequest request, MajaziDbContext db, CancellationToken ct) =>
{
    var item = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound(new { error = "برند پیدا نشد." });
    var name = request.Name?.Trim();
    var slug = request.Slug?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug))
        return Results.BadRequest(new { error = "نام و شناسه برند الزامی است." });
    if (name.Length > 180 || slug.Length > 100)
        return Results.BadRequest(new { error = "طول نام یا شناسه برند بیش از حد مجاز است." });
    if (await db.Brands.AnyAsync(x => x.Id != id && x.OrganizationId == item.OrganizationId && x.Slug == slug, ct))
        return Results.Conflict(new { error = "این شناسه برند قبلاً در سازمان ثبت شده است." });
    item.Name = name;
    item.Slug = slug;
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "شناسه برند تکراری است." }); }
    return Results.Ok(new { item.Id, item.OrganizationId, item.Name, item.Slug, item.CreatedAt });
});

app.MapDelete("/api/brands/{id:guid}", async (Guid id, MajaziDbContext db, CancellationToken ct) =>
{
    var item = await db.Brands.FirstOrDefaultAsync(x => x.Id == id, ct);
    if (item is null) return Results.NotFound(new { error = "برند پیدا نشد." });
    db.Brands.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.Run();

public sealed record CreateOrganizationRequest(string? Name, string? Slug);
public sealed record CreateBrandRequest(string? Name, string? Slug);
