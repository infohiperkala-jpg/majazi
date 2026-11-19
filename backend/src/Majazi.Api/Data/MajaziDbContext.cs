using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Majazi.Api.Data;

public sealed class MajaziDbContext(DbContextOptions<MajaziDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<CrmContact> CrmContacts => Set<CrmContact>();
    public DbSet<CrmLead> CrmLeads => Set<CrmLead>();
    public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();
    public DbSet<MarketingCampaign> MarketingCampaigns => Set<MarketingCampaign>();
    public DbSet<SocialChannelConnection> SocialChannelConnections => Set<SocialChannelConnection>();
    public DbSet<InboxConversation> InboxConversations => Set<InboxConversation>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<ContentDraft> ContentDrafts => Set<ContentDraft>();
    public DbSet<ContentDraftVersion> ContentDraftVersions => Set<ContentDraftVersion>();
    public DbSet<ContentDraftAttachment> ContentDraftAttachments => Set<ContentDraftAttachment>();
    public DbSet<ContentApprovalRecord> ContentApprovalRecords => Set<ContentApprovalRecord>();
    public DbSet<StatusDefinition> StatusDefinitions => Set<StatusDefinition>();
    public DbSet<TextTemplate> TextTemplates => Set<TextTemplate>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<KnowledgeArticleRevision> KnowledgeArticleRevisions => Set<KnowledgeArticleRevision>();
    public DbSet<OrganizationAuditEvent> OrganizationAuditEvents => Set<OrganizationAuditEvent>();
    public DbSet<OrganizationAiProviderSetting> OrganizationAiProviderSettings => Set<OrganizationAiProviderSetting>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContentDraftAttachment>(e =>
        {
            e.ToTable("content_draft_attachments"); e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(160).IsRequired();
            e.Property(x => x.Data).HasColumnType("bytea").IsRequired();
            e.HasIndex(x => x.DraftId).IsUnique();
            e.HasOne<ContentDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationRun>(e =>
        {
            e.Property(x => x.EventKey).HasMaxLength(128);
            e.HasIndex(x => new { x.OrganizationId, x.RuleId, x.EventKey }).IsUnique().HasFilter("\"EventKey\" IS NOT NULL");
        });

        modelBuilder.Entity<SalesOrder>(e =>
        {
            e.ToTable("sales_orders"); e.HasKey(x => x.Id);
            e.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Subtotal).HasPrecision(18, 2); e.Property(x => x.Discount).HasPrecision(18, 2);
            e.Property(x => x.Tax).HasPrecision(18, 2); e.Property(x => x.Total).HasPrecision(18, 2);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OrganizationId, x.OrderNumber }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CrmContact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<SalesOrderLine>(e =>
        {
            e.ToTable("sales_order_lines"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(240).IsRequired(); e.Property(x => x.Sku).HasMaxLength(100);
            e.Property(x => x.Quantity).HasPrecision(18, 3); e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.Discount).HasPrecision(18, 2); e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.HasIndex(x => new { x.OrderId, x.SortOrder });
            e.HasOne<SalesOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PaymentTransaction>(e =>
        {
            e.ToTable("payment_transactions"); e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Method).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.Reference).HasMaxLength(180);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.OrderId, x.CreatedAt });
            e.HasOne<SalesOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationAiProviderSetting>(e =>
        {
            e.ToTable("organization_ai_provider_settings"); e.HasKey(x => x.Id);
            e.Property(x => x.BaseUrl).HasMaxLength(500).IsRequired();
            e.Property(x => x.Model).HasMaxLength(160).IsRequired();
            e.Property(x => x.EncryptedApiKey).HasMaxLength(12000);
            e.HasIndex(x => x.OrganizationId).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationAuditEvent>(e =>
        {
            e.ToTable("organization_audit_events"); e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            e.Property(x => x.MetadataJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => new { x.OrganizationId, x.Action, x.CreatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeArticle>(e =>
        {
            e.ToTable("knowledge_articles"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(160).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.Body).HasMaxLength(60000).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.Tags).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.UpdatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KnowledgeArticleRevision>(e =>
        {
            e.ToTable("knowledge_article_revisions"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            e.Property(x => x.Body).HasMaxLength(60000).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.Tags).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ChangeNote).HasMaxLength(300);
            e.HasIndex(x => new { x.ArticleId, x.Version }).IsUnique();
            e.HasOne<KnowledgeArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationRule>(e =>
        {
            e.ToTable("automation_rules"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Trigger).HasMaxLength(40).IsRequired();
            e.Property(x => x.ConditionField).HasMaxLength(40).IsRequired();
            e.Property(x => x.ConditionOperator).HasMaxLength(20).IsRequired();
            e.Property(x => x.ConditionValue).HasMaxLength(240);
            e.Property(x => x.ActionType).HasMaxLength(40).IsRequired();
            e.Property(x => x.ActivityType).HasMaxLength(32);
            e.Property(x => x.ActionValue).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Trigger, x.IsEnabled });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AutomationRun>(e =>
        {
            e.ToTable("automation_runs"); e.HasKey(x => x.Id);
            e.Property(x => x.Trigger).HasMaxLength(40).IsRequired();
            e.Property(x => x.SubjectType).HasMaxLength(24).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Result).HasMaxLength(1000);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.OrganizationId, x.RuleId, x.CreatedAt });
            e.HasIndex(x => x.RuleId);
            e.HasOne<AutomationRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TextTemplate>(e =>
        {
            e.ToTable("text_templates"); e.HasKey(x => x.Id);
            e.Property(x => x.Module).HasMaxLength(80).IsRequired();
            e.Property(x => x.Field).HasMaxLength(80).IsRequired();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Content).HasMaxLength(8000).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Module, x.Field, x.Name }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Module, x.Field, x.SortOrder });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StatusDefinition>(e =>
        {
            e.ToTable("status_definitions"); e.HasKey(x => x.Id);
            e.Property(x => x.Module).HasMaxLength(80).IsRequired();
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Label).HasMaxLength(100).IsRequired();
            e.Property(x => x.Color).HasMaxLength(7).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => new { x.OrganizationId, x.Module, x.Key }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Module, x.SortOrder });
        });

        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(180).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });
        modelBuilder.Entity<Brand>(e =>
        {
            e.ToTable("brands"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(180).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            e.HasOne(x => x.Organization).WithMany(x => x.Brands).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("app_users"); e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });
        modelBuilder.Entity<OrganizationMembership>(e =>
        {
            e.ToTable("organization_memberships"); e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.UserId, x.OrganizationId }).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Organization).WithMany(x => x.Memberships).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmContact>(e =>
        {
            e.ToTable("crm_contacts"); e.HasKey(x => x.Id);
            e.Property(x => x.FullName).HasMaxLength(180).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Company).HasMaxLength(180);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<CrmLead>(e =>
        {
            e.ToTable("crm_leads"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Stage).HasMaxLength(32).IsRequired();
            e.Property(x => x.EstimatedValue).HasPrecision(18, 2);
            e.Property(x => x.Source).HasMaxLength(120);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.Stage, x.CreatedAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<MarketingCampaign>(e =>
        {
            e.ToTable("marketing_campaigns"); e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Objective).HasMaxLength(1000);
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.Budget).HasPrecision(18, 2);
            e.Property(x => x.ActualSpend).HasPrecision(18, 2);
            e.Property(x => x.AttributedRevenue).HasPrecision(18, 2);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.StartsAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SocialChannelConnection>(e =>
        {
            e.ToTable("social_channel_connections"); e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(180).IsRequired();
            e.Property(x => x.ExternalAccountId).HasMaxLength(180);
            e.Property(x => x.EncryptedAccessToken).HasMaxLength(12000);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Provider, x.ExternalAccountId }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
        });


        modelBuilder.Entity<InboxConversation>(e =>
        {
            e.ToTable("inbox_conversations"); e.HasKey(x => x.Id);
            e.Property(x => x.ExternalConversationId).HasMaxLength(180);
            e.Property(x => x.Subject).HasMaxLength(240);
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.LastMessageAt });
            e.HasIndex(x => new { x.ChannelConnectionId, x.ExternalConversationId }).IsUnique();
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ChannelConnection).WithMany().HasForeignKey(x => x.ChannelConnectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages"); e.HasKey(x => x.Id);
            e.Property(x => x.ExternalMessageId).HasMaxLength(180);
            e.Property(x => x.Direction).HasMaxLength(12).IsRequired();
            e.Property(x => x.Body).HasMaxLength(10000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.Property(x => x.ProviderPayload).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasIndex(x => new { x.ConversationId, x.ExternalMessageId }).IsUnique();
            e.HasOne(x => x.Conversation).WithMany(x => x.Messages).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentDraft>(e =>
        {
            e.ToTable("content_drafts"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Content).HasMaxLength(30000).IsRequired();
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.Tone).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(24).IsRequired();
            e.Property(x => x.Prompt).HasMaxLength(4000);
            e.Property(x => x.AiModel).HasMaxLength(120);
            e.Property(x => x.PublishTarget).HasMaxLength(180);
            e.Property(x => x.PublishError).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.ScheduledAt });
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.UpdatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Versions).WithOne(x => x.Draft).HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ContentDraftVersion>(e =>
        {
            e.ToTable("content_draft_versions"); e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(240).IsRequired();
            e.Property(x => x.Content).HasMaxLength(30000).IsRequired();
            e.Property(x => x.ChangeNote).HasMaxLength(300).IsRequired();
            e.HasIndex(x => new { x.DraftId, x.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<ContentApprovalRecord>(e =>
        {
            e.ToTable("content_approval_records"); e.HasKey(x => x.Id);
            e.Property(x => x.Decision).HasMaxLength(20).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.HasIndex(x => new { x.DraftId, x.ReviewRound, x.StageNumber }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasOne<ContentDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmActivity>(e =>
        {
            e.ToTable("crm_activities"); e.HasKey(x => x.Id);
            e.Property(x => x.ActivityType).HasMaxLength(32).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            e.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
