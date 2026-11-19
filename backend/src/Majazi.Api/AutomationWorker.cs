using System.Text.Json;
using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Majazi.Api;

public sealed class AutomationWorker(IServiceScopeFactory scopeFactory, ILogger<AutomationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Automation queue processing failed."); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MajaziDbContext>();
        var stale = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.AutomationRuns.Where(x => x.Status == "Running" && x.StartedAt < stale)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Failed")
                .SetProperty(x => x.Error, "اجرای قبلی بیش از ۵ دقیقه بدون نتیجه مانده است؛ برای جلوگیری از اجرای تکراری، خودکار تکرار نشد.")
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), ct);

        var ids = await db.AutomationRuns.AsNoTracking().Where(x => x.Status == "Pending")
            .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            var now = DateTimeOffset.UtcNow;
            var claimed = await db.AutomationRuns.Where(x => x.Id == id && x.Status == "Pending")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Running")
                    .SetProperty(x => x.StartedAt, now)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), ct);
            if (claimed == 0) continue;
            var run = await db.AutomationRuns.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null) continue;
            try
            {
                var rule = await db.AutomationRules.SingleOrDefaultAsync(x => x.Id == run.RuleId && x.OrganizationId == run.OrganizationId, ct);
                if (rule is null || !rule.IsEnabled)
                {
                    run.Status = "Skipped"; run.Result = "قانون حذف یا غیرفعال شده بود.";
                }
                else
                {
                    var contact = run.SubjectType == "Contact"
                        ? await db.CrmContacts.SingleOrDefaultAsync(x => x.Id == run.SubjectId && x.OrganizationId == run.OrganizationId, ct)
                        : null;
                    var lead = run.SubjectType == "Lead"
                        ? await db.CrmLeads.SingleOrDefaultAsync(x => x.Id == run.SubjectId && x.OrganizationId == run.OrganizationId, ct)
                        : null;
                    var draft = run.SubjectType == "ContentDraft"
                        ? await db.ContentDrafts.SingleOrDefaultAsync(x => x.Id == run.SubjectId && x.OrganizationId == run.OrganizationId, ct)
                        : null;
                    if (contact is null && lead is null && draft is null) throw new InvalidOperationException("رکورد محرک دیگر در سازمان وجود ندارد.");
                    if (draft is not null && (draft.Status != "Approved" || rule.Trigger != "ContentApproved"))
                        throw new InvalidOperationException("محتوا دیگر در وضعیت تأیید نهایی نیست.");
                    if (!Matches(rule, contact, lead))
                    {
                        run.Status = "Skipped"; run.Result = "شرط قانون برقرار نبود.";
                    }
                    else if (rule.ActionType == "CreateActivity")
                    {
                        var summary = Expand(rule.ActionValue, contact, lead, draft);
                        var activity = new CrmActivity
                        {
                            OrganizationId = run.OrganizationId, ContactId = contact?.Id ?? lead?.ContactId,
                            LeadId = lead?.Id, CreatedByUserId = rule.CreatedByUserId,
                            ActivityType = rule.ActivityType ?? "Task", Summary = summary
                        };
                        db.CrmActivities.Add(activity);
                        run.Status = "Succeeded"; run.Result = "فعالیت CRM ایجاد شد: " + summary;
                    }
                    else if (rule.ActionType == "UpdateLeadStage")
                    {
                        if (lead is null) throw new InvalidOperationException("این اقدام فقط برای محرک‌های مربوط به سرنخ فروش قابل اجراست.");
                        var target = rule.ActionValue.Trim();
                        var hasDefinitions = await db.StatusDefinitions.AnyAsync(x => x.OrganizationId == run.OrganizationId && x.Module == "crm.lead", ct);
                        var allowed = hasDefinitions
                            ? await db.StatusDefinitions.AnyAsync(x => x.OrganizationId == run.OrganizationId && x.Module == "crm.lead" && x.IsActive && x.Key.ToLower() == target.ToLower(), ct)
                            : new[] { "new", "qualified", "proposal", "won", "lost" }.Contains(target, StringComparer.OrdinalIgnoreCase);
                        if (!allowed) throw new InvalidOperationException("مرحله مقصد دیگر فعال یا معتبر نیست.");
                        lead.Stage = target; lead.UpdatedAt = DateTimeOffset.UtcNow;
                        run.Status = "Succeeded"; run.Result = "مرحله سرنخ به «" + target + "» تغییر کرد.";
                    }
                    else throw new InvalidOperationException("نوع اقدام این قانون پشتیبانی نمی‌شود.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Status = "Failed";
                run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                logger.LogWarning(ex, "Automation run {RunId} failed.", run.Id);
            }
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool Matches(AutomationRule rule, CrmContact? contact, CrmLead? lead)
    {
        if (rule.ConditionField == "Any" || string.IsNullOrWhiteSpace(rule.ConditionValue)) return true;
        var actual = rule.ConditionField switch
        {
            "LeadStage" => lead?.Stage,
            "LeadSource" => lead?.Source,
            "ContactCompany" => contact?.Company,
            _ => null
        };
        if (actual is null) return false;
        return rule.ConditionOperator == "Contains"
            ? actual.Contains(rule.ConditionValue, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actual, rule.ConditionValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string Expand(string template, CrmContact? contact, CrmLead? lead, ContentDraft? draft) => template
        .Replace("{contactName}", contact?.FullName ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{leadTitle}", lead?.Title ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{leadStage}", lead?.Stage ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{leadSource}", lead?.Source ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{contentTitle}", draft?.Title ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{contentChannel}", draft?.Channel ?? "", StringComparison.OrdinalIgnoreCase);
}
