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
                    "فرآیند انتشار بیش از ۱۰ دقیقه بدون ثبت نتیجه باقی مانده است. برای جلوگیری از ارسال تکراری، ارسال خودکار تکرار نشد؛ مقصد پیام‌رسان را بررسی و سپس در صورت نیاز دوباره زمان‌بندی کنید.")
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
                    (x.Provider == "Telegram" || x.Provider == "Eitaa" || x.Provider == "WhatsApp") && x.Status == "Connected", ct);
                if (connection?.EncryptedAccessToken is null)
                    throw new InvalidOperationException("اتصال پیام‌رسان انتخاب‌شده دیگر فعال نیست.");
                if (connection.Provider is "Telegram" or "WhatsApp" && draft.Content.Length > 4096)
                    throw new InvalidOperationException("متن بیش از محدودیت ۴۰۹۶ نویسه پیام‌رسان است؛ محتوا را کوتاه کنید.");
                if (draft.Channel != connection.Provider)
                    throw new InvalidOperationException("پلتفرم پیش‌نویس با اتصال انتشار یکسان نیست.");

                var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
                var protector = protection.CreateProtector("Majazi.SocialConnections.v1");
                var token = protector.Unprotect(connection.EncryptedAccessToken);
                if (connection.Provider == "WhatsApp")
                {
                    var version = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Meta:GraphApiVersion"] ?? "v22.0";
                    var phoneNumberId = connection.ExternalAccountId;
                    if (string.IsNullOrWhiteSpace(phoneNumberId))
                        throw new InvalidOperationException("شناسه Phone Number ID برای اتصال واتساپ ثبت نشده است.");
                    var attachment = await db.ContentDraftAttachments.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.DraftId == draft.Id, ct);
                    if (attachment is null)
                    {
                        using var outbound = new HttpRequestMessage(HttpMethod.Post,
                            $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(phoneNumberId)}/messages");
                        outbound.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                        outbound.Content = JsonContent.Create(new
                        {
                            messaging_product = "whatsapp", recipient_type = "individual",
                            to = draft.PublishTarget!.Trim().TrimStart('+'), type = "text",
                            text = new { preview_url = false, body = draft.Content }
                        });
                        using var response = await httpClientFactory.CreateClient("MetaOAuth").SendAsync(outbound, ct);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("WhatsApp Cloud API پیام را نپذیرفت؛ شماره مقصد، مجوزها و پنجره ۲۴ ساعته پیام‌رسانی را بررسی کنید.");
                        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (!json.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                            throw new InvalidOperationException("WhatsApp شناسه پیام ارسالی را برنگرداند.");
                    }
                    else
                    {
                        if (draft.Content.Length > 1024)
                            throw new InvalidOperationException("متن توضیح همراه رسانه واتساپ حداکثر ۱۰۲۴ نویسه است.");
                        using var multipart = new MultipartFormDataContent();
                        multipart.Add(new StringContent("whatsapp"), "messaging_product");
                        multipart.Add(new StringContent(attachment.ContentType), "type");
                        var upload = new ByteArrayContent(attachment.Data);
                        upload.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
                        multipart.Add(upload, "file", attachment.FileName);
                        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post,
                            $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(phoneNumberId)}/media");
                        uploadRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                        uploadRequest.Content = multipart;
                        using var uploadResponse = await httpClientFactory.CreateClient("MetaOAuth").SendAsync(uploadRequest, ct);
                        if (!uploadResponse.IsSuccessStatusCode)
                            throw new InvalidOperationException("WhatsApp Cloud API بارگذاری رسانه را نپذیرفت؛ نوع و اندازه فایل را بررسی کنید.");
                        using var uploadJson = await JsonDocument.ParseAsync(await uploadResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (!uploadJson.RootElement.TryGetProperty("id", out var mediaIdElement) || string.IsNullOrWhiteSpace(mediaIdElement.GetString()))
                            throw new InvalidOperationException("Meta شناسه رسانه بارگذاری‌شده را برنگرداند.");
                        var mediaId = mediaIdElement.GetString()!;
                        var mediaType = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image"
                            : attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "document";
                        object media = mediaType switch
                        {
                            "image" => new { id = mediaId, caption = draft.Content },
                            "video" => new { id = mediaId, caption = draft.Content },
                            _ => new { id = mediaId, caption = draft.Content, filename = attachment.FileName }
                        };
                        var payload = new Dictionary<string, object>
                        {
                            ["messaging_product"] = "whatsapp",
                            ["recipient_type"] = "individual",
                            ["to"] = draft.PublishTarget!.Trim().TrimStart('+'),
                            ["type"] = mediaType,
                            [mediaType] = media
                        };
                        using var outbound = new HttpRequestMessage(HttpMethod.Post,
                            $"https://graph.facebook.com/{version}/{Uri.EscapeDataString(phoneNumberId)}/messages");
                        outbound.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                        outbound.Content = JsonContent.Create(payload);
                        using var response = await httpClientFactory.CreateClient("MetaOAuth").SendAsync(outbound, ct);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("WhatsApp Cloud API ارسال رسانه را نپذیرفت؛ شماره مقصد و پنجره ۲۴ ساعته پیام‌رسانی را بررسی کنید.");
                        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (!json.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                            throw new InvalidOperationException("WhatsApp شناسه پیام رسانه‌ای را برنگرداند.");
                    }
                }
                else if (connection.Provider == "Telegram")
                {
                    var attachment = await db.ContentDraftAttachments.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.DraftId == draft.Id, ct);
                    HttpResponseMessage response;
                    if (attachment is null)
                    {
                        response = await httpClientFactory.CreateClient("TelegramBotApi")
                            .PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
                                new { chat_id = draft.PublishTarget, text = draft.Content, disable_web_page_preview = false }, ct);
                    }
                    else
                    {
                        if (draft.Content.Length > 1024)
                            throw new InvalidOperationException("متن همراه فایل تلگرام حداکثر ۱۰۲۴ نویسه است؛ محتوا را کوتاه کنید.");
                        using var multipart = new MultipartFormDataContent();
                        multipart.Add(new StringContent(draft.PublishTarget!), "chat_id");
                        multipart.Add(new StringContent(draft.Content), "caption");
                        var fileContent = new ByteArrayContent(attachment.Data);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
                        multipart.Add(fileContent, "document", attachment.FileName);
                        response = await httpClientFactory.CreateClient("TelegramBotApi")
                            .PostAsync($"https://api.telegram.org/bot{token}/sendDocument", multipart, ct);
                    }
                    using (response)
                    {
                        var payload = await response.Content.ReadAsStringAsync(ct);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("Telegram API returned HTTP " + (int)response.StatusCode + ".");
                        using var json = JsonDocument.Parse(payload);
                        if (!json.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                            throw new InvalidOperationException("Telegram did not accept the publication.");
                    }
                }
                else
                {
                    try
                    {
                        var attachment = await db.ContentDraftAttachments.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.DraftId == draft.Id, ct);
                        var method = attachment is null ? "sendMessage" : "sendFile";
                        var endpoint = $"https://eitaayar.ir/api/{Uri.EscapeDataString(token)}/{method}";
                        HttpContent requestContent;
                        if (attachment is null)
                        {
                            requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                ["chat_id"] = draft.PublishTarget!,
                                ["text"] = draft.Content
                            });
                        }
                        else
                        {
                            var multipart = new MultipartFormDataContent();
                            multipart.Add(new StringContent(draft.PublishTarget!), "chat_id");
                            multipart.Add(new StringContent(draft.Content), "caption");
                            var fileContent = new ByteArrayContent(attachment.Data);
                            fileContent.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
                            multipart.Add(fileContent, "file", attachment.FileName);
                            requestContent = multipart;
                        }
                        using (requestContent)
                        using (var response = await httpClientFactory.CreateClient("EitaaYar").PostAsync(endpoint, requestContent, ct))
                        {
                            if (!response.IsSuccessStatusCode)
                                throw new InvalidOperationException("EitaaYar returned HTTP " + (int)response.StatusCode + ".");
                            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                            if (!json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                                throw new InvalidOperationException("EitaaYar did not accept the publication.");
                        }
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
                logger.LogInformation("Content draft {DraftId} published to {Provider}.", draft.Id, draft.Channel);
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
