using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<MajaziDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks();
var app = builder.Build();
app.UseCors();
app.MapHealthChecks("/health");
app.MapGet("/api", () => Results.Ok(new { name = "MAJAZI API", version = "0.1.0", status = "running" }));
app.MapGet("/api/organizations", async (MajaziDbContext db, CancellationToken ct) => Results.Ok(await db.Organizations.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Slug, x.CreatedAt }).ToListAsync(ct)));
app.MapPost("/api/organizations", async (CreateOrganizationRequest request, MajaziDbContext db, CancellationToken ct) => {
 var name=request.Name?.Trim(); var slug=request.Slug?.Trim().ToLowerInvariant();
 if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(slug)) return Results.BadRequest(new { error="نام و شناسه سازمان الزامی است." });
 if(await db.Organizations.AnyAsync(x=>x.Slug==slug,ct)) return Results.Conflict(new { error="این شناسه سازمان قبلاً ثبت شده است." });
 var item=new Organization{Name=name,Slug=slug}; db.Organizations.Add(item); await db.SaveChangesAsync(ct);
 return Results.Created($"/api/organizations/{item.Id}",new {item.Id,item.Name,item.Slug,item.CreatedAt});
});
app.MapGet("/api/organizations/{id:guid}/brands", async (Guid id,MajaziDbContext db,CancellationToken ct)=>{
 if(!await db.Organizations.AnyAsync(x=>x.Id==id,ct)) return Results.NotFound(new {error="سازمان پیدا نشد."});
 return Results.Ok(await db.Brands.AsNoTracking().Where(x=>x.OrganizationId==id).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.OrganizationId,x.Name,x.Slug,x.CreatedAt}).ToListAsync(ct));
});
app.MapPost("/api/organizations/{id:guid}/brands",async(Guid id,CreateBrandRequest request,MajaziDbContext db,CancellationToken ct)=>{
 if(!await db.Organizations.AnyAsync(x=>x.Id==id,ct)) return Results.NotFound(new{error="سازمان پیدا نشد."});
 var name=request.Name?.Trim();var slug=request.Slug?.Trim().ToLowerInvariant();
 if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(slug)) return Results.BadRequest(new{error="نام و شناسه برند الزامی است."});
 if(await db.Brands.AnyAsync(x=>x.OrganizationId==id&&x.Slug==slug,ct)) return Results.Conflict(new{error="این شناسه برند قبلاً ثبت شده است."});
 var item=new Brand{OrganizationId=id,Name=name,Slug=slug};db.Brands.Add(item);await db.SaveChangesAsync(ct);
 return Results.Created($"/api/brands/{item.Id}",new{item.Id,item.OrganizationId,item.Name,item.Slug,item.CreatedAt});
});
app.Run();
public sealed record CreateOrganizationRequest(string? Name,string? Slug);
public sealed record CreateBrandRequest(string? Name,string? Slug);
