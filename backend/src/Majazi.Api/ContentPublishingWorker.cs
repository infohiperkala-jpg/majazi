using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Majazi.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Majazi.Api;

public sealed class ContentPublishingWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<ContentPublishingWorker> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Content publishing queue processing failed."); }
            try { await Task.Delay(_pollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessDueAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MajaziDbContext>();
        var dueIds = await db.ContentDrafts.AsNoTracking()
            .Where(x => x.Status == "Scheduled" && x.ScheduledAt <= DateTimeOffset.UtcNow)
            .OrderBy(x => x.ScheduledAt).Select(x => x.Id).Take(10).ToListAsync(ct);

        foreach (var id in dueIds)
        {
            var claimed = await db.ContentDrafts.Where(x => x.Id == id && x.Status == "Scheduled" &&
                    x.ScheduledAt <= DateTimeOffset.UtcNow)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "Publishing"), ct);
            if (claimed == 0) continue;

            var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (draft is null) continue;
            try
            {
                if (draft.PublishConnectionId is null || string.IsNullOrWhiteSpace(draft.PublishTarget))
                    throw new InvalidOperationException("مقصد انتشار یا اتصال کانال مشخص نشده است.");
                var connection = await db.SocialChannelConnections.SingleOrDefaultAsync(x =>
                    x.Id == draft.PublishConnectionId && x.OrganizationId == draft.OrganizationId &&
                    x.Provider == "Telegram" && x.Status == "Connected", ct);
                if (connection?.EncryptedAccessToken is null)
                    throw new InvalidOperationException("اتصال تلگرام دیگر فعال نیست.");
                if (draft.Content.Length > 4096)
                    throw new InvalidOperationException("متن بیش از محدودیت ۴۰۹۶ نویسه تلگرام است؛ محتوا را کوتاه کنید.");

                var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
                var protector = protection.CreateProtector("Majazi.SocialConnections.v1");
                var token = protector.Unprotect(connection.EncryptedAccessToken);
                using var response = await httpClientFactory.CreateClient("TelegramBotApi")
                    .PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
                        new { chat_id = draft.PublishTarget, text = draft.Content, disable_web_page_preview = false }, ct);
                var payload = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Telegram API returned HTTP " + (int)response.StatusCode + ".");
                using var json = JsonDocument.Parse(payload);
                if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    throw new InvalidOperationException("Telegram did not accept the publication.");

                draft.Status = "Published";
                draft.PublishedAt = DateTimeOffset.UtcNow;
                draft.PublishError = null;
                draft.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Content draft {DraftId} published to Telegram.", draft.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                draft.Status = "PublishFailed";
                draft.PublishError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                draft.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogWarning(ex, "Publishing content draft {DraftId} failed.", draft.Id);
            }
        }
    }
}
