using System.Net.Http.Json;
using System.Text.Json;
using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Majazi.Api;

public sealed class GrowthAgentWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory clients,
    IConfiguration configuration,
    IDataProtectionProvider protection,
    ILogger<GrowthAgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueOrganizations(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Autonomous growth agent cycle failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunDueOrganizations(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MajaziDbContext>();
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var ids = await db.Organizations.AsNoTracking()
            .Where(x => x.GrowthAgentEnabled && (x.GrowthAgentLastRunAt == null || x.GrowthAgentLastRunAt < today))
            .OrderBy(x => x.GrowthAgentLastRunAt).Select(x => x.Id).Take(20).ToListAsync(ct);

        foreach (var id in ids)
        {
            var claimedAt = DateTimeOffset.UtcNow;
            var claimed = await db.Organizations.Where(x => x.Id == id && x.GrowthAgentEnabled &&
                    (x.GrowthAgentLastRunAt == null || x.GrowthAgentLastRunAt < today))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.GrowthAgentLastRunAt, claimedAt), ct);
            if (claimed == 0) continue;

            try
            {
                var savedAi = await db.OrganizationAiProviderSettings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == id, ct);
                var key = configuration["AI:ApiKey"];
                var baseUrl = configuration["AI:BaseUrl"];
                var model = configuration["AI:Model"];
                if (savedAi is not null)
                {
                    key = configuration["AI:ApiKey"];
                    if (!string.IsNullOrWhiteSpace(savedAi.EncryptedApiKey))
                    {
                        try { key = protection.CreateProtector("Majazi.AIProviderSettings.v1", id.ToString("N")).Unprotect(savedAi.EncryptedApiKey); }
                        catch (CryptographicException) { key = configuration["AI:ApiKey"]; }
                    }
                    baseUrl = savedAi.BaseUrl;
                    model = savedAi.Model;
                    if (!savedAi.IsEnabled) continue;
                }
                baseUrl = baseUrl?.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model) ||
                    !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)) continue;

                var org = await db.Organizations.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { x.Name }).SingleAsync(ct);
                var channels = await db.SocialChannelConnections.AsNoTracking()
                    .Where(x => x.OrganizationId == id)
                    .Select(x => new { x.Provider, x.DisplayName, x.Status }).ToListAsync(ct);
                var campaigns = await db.MarketingCampaigns.AsNoTracking().Where(x => x.OrganizationId == id)
                    .OrderByDescending(x => x.UpdatedAt).Take(12)
                    .Select(x => new { x.Name, x.Channel, x.Status, x.Budget, x.ActualSpend, x.AttributedRevenue, x.Impressions, x.Clicks, x.Conversions }).ToListAsync(ct);
                var ownerId = await db.OrganizationMemberships.Where(x => x.OrganizationId == id && x.Role == "Owner")
                    .Select(x => x.UserId).FirstOrDefaultAsync(ct);
                if (ownerId == Guid.Empty) continue;

                var context = JsonSerializer.Serialize(new { organization = org.Name, channels, recentCampaigns = campaigns });
                var system = "You are an expert 24/7 growth and revenue strategist for Iranian and international social platforms. Return ONLY JSON with title and content. Write in Persian. Build a practical daily action plan based only on supplied facts. Clearly separate connected platforms from unconnected ones. Never recommend fake views/followers, unsolicited messages, forced group additions, scraping, credential sharing, or policy evasion. For platforms without verified APIs, recommend compliant manual tasks and mark them as manual. Include prioritized tasks, channel-specific content ideas, lead-generation and sales conversion steps, measurable KPIs, and risks. Do not invent performance numbers.";
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                req.Content = JsonContent.Create(new {
                    model, temperature = 0.35,
                    response_format = new { type = "json_object" },
                    messages = new[] { new { role = "system", content = system }, new { role = "user", content = "Create today's actionable growth plan from this organization data: " + context } }
                });
                using var response = await clients.CreateClient("AIProvider").SendAsync(req, ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("AI provider returned HTTP " + (int)response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var text = payload.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("AI returned an empty plan.");
                using var result = JsonDocument.Parse(text);
                var title = result.RootElement.GetProperty("title").GetString()?.Trim();
                var content = result.RootElement.GetProperty("content").GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(content) || content.Length > 30000)
                    throw new InvalidOperationException("AI plan format was invalid.");

                var draft = new ContentDraft {
                    OrganizationId = id, CreatedByUserId = ownerId,
                    Title = string.IsNullOrWhiteSpace(title) ? "برنامه رشد روزانه" : title[..Math.Min(title.Length, 240)],
                    Content = content, Channel = "General", Tone = "Growth strategy",
                    Prompt = "برنامه روزانه خودکار عامل رشد MAJAZI", AiModel = model
                };
                draft.Versions.Add(new ContentDraftVersion {
                    DraftId = draft.Id, VersionNumber = 1, Title = draft.Title, Content = content,
                    ChangeNote = "برنامه روزانه تولیدشده توسط عامل خودکار رشد", CreatedByUserId = ownerId
                });
                db.ContentDrafts.Add(draft);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Daily growth plan {DraftId} created for organization {OrganizationId}.", draft.Id, id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Growth agent failed for organization {OrganizationId}.", id); }
        }
    }
}
