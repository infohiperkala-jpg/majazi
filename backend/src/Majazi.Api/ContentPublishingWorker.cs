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
        // A process can die after claiming a job or after Telegram accepted it but
        // before the database was updated. Never retry such jobs automatically:
        // doing so could publish duplicate messages. Mark old claims for operator review.
        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.ContentDrafts
            .Where(x => x.Status == "Publishing" && x.UpdatedAt < staleBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "PublishFailed")
                .SetProperty(x => x.PublishError,
                    "فرآیند انتشار بیش از ۱۰ دقیقه بدون ثبت نتیجه باقی مانده است. برای جلوگیری از ارسال تکراری، ارسال خودکار تکرار نشد؛ مقصد تلگرام را بررسی و سپس در صورت نیاز دوباره زمان‌بندی کنید.")
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);

        var dueIds = await db.ContentDrafts.AsNoTracking()
            .Where(x => x.Status == "Scheduled" && x.ScheduledAt <= DateTimeOffset.UtcNow)
            .OrderBy(x => x.ScheduledAt).Select(x => x.Id).Take(10).ToListAsync(ct);

        foreach (var id in dueIds)
        {
            var claimed = await db.ContentDrafts.Where(x => x.Id == id && x.Status == "Scheduled" &&
                    x.ScheduledAt <= DateTimeOffset.UtcNow)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Publishing")
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.PublishError, (string?)null), ct);
            if (claimed == 0) continue;

            var draft = await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (draft is null) continue;
            try
            {
                if (draft.PublishConnectionId is null || string.IsNullOrWhiteSpace(draft.PublishTarget))
                    throw new InvalidOperationException("مقصد انتشار یا اتصال کانال مشخص نشده است.");
                var connection = await db.SocialChannelConnections.SingleOrDefaultAsync(x =>
                    x.Id == draft.PublishConnectionId && x.OrganizationId == draft.OrganizationId &&
                    (x.Provider == "Telegram" || x.Provider == "Eitaa") && x.Status == "Connected", ct);
                if (connection?.EncryptedAccessToken is null)
                    throw new InvalidOperationException("اتصال پیام‌رسان انتخاب‌شده دیگر فعال نیست.");
                if (connection.Provider == "Telegram" && draft.Content.Length > 4096)
                    throw new InvalidOperationException("متن بیش از محدودیت ۴۰۹۶ نویسه تلگرام است؛ محتوا را کوتاه کنید.");
                if (draft.Channel != connection.Provider)
                    throw new InvalidOperationException("پلتفرم پیش‌نویس با اتصال انتشار یکسان نیست.");

                var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
                var protector = protection.CreateProtector("Majazi.SocialConnections.v1");
                var token = protector.Unprotect(connection.EncryptedAccessToken);
                if (connection.Provider == "Telegram")
                {
                    using var response = await httpClientFactory.CreateClient("TelegramBotApi")
                        .PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
                            new { chat_id = draft.PublishTarget, text = draft.Content, disable_web_page_preview = false }, ct);
                    var payload = await response.Content.ReadAsStringAsync(ct);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Telegram API returned HTTP " + (int)response.StatusCode + ".");
                    using var json = JsonDocument.Parse(payload);
                    if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                        throw new InvalidOperationException("Telegram did not accept the publication.");
                }
                else
                {
                    try
                    {
                        var endpoint = $"https://eitaayar.ir/api/{Uri.EscapeDataString(token)}/sendMessage";
                        using var response = await httpClientFactory.CreateClient("EitaaYar").PostAsync(endpoint,
                            new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                ["chat_id"] = draft.PublishTarget!,
                                ["text"] = draft.Content
                            }), ct);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("EitaaYar returned HTTP " + (int)response.StatusCode + ".");
                        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (!json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("EitaaYar did not accept the publication.");
                    }
                    catch (HttpRequestException)
                    {
                        // Avoid persisting an exception that may include the credential-bearing request URI.
                        throw new InvalidOperationException("ارتباط با API ایتایار برقرار نشد؛ مسیر شبکه و سرویس مقصد را بررسی کنید.");
                    }
                }

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
