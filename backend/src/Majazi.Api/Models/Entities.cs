namespace Majazi.Api.Models;

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool GrowthAgentEnabled { get; set; }
    public DateTimeOffset? GrowthAgentLastRunAt { get; set; }
    public ICollection<Brand> Brands { get; set; } = new List<Brand>();
    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();
}

public sealed class Brand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
}

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
}

public sealed class OrganizationMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AppUser? User { get; set; }
    public Organization? Organization { get; set; }
}


public sealed class CrmContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? BrandId { get; set; }
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
    public Brand? Brand { get; set; }
}

public sealed class CrmLead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? BrandId { get; set; }
    public Guid? ContactId { get; set; }
    public required string Title { get; set; }
    public string Stage { get; set; } = "New";
    public decimal? EstimatedValue { get; set; }
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
    public Brand? Brand { get; set; }
    public CrmContact? Contact { get; set; }
}

public sealed class CrmActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public required string ActivityType { get; set; }
    public required string Summary { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
    public CrmContact? Contact { get; set; }
    public CrmLead? Lead { get; set; }
}

public sealed class MarketingCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? BrandId { get; set; }
    public required string Name { get; set; }
    public string? Objective { get; set; }
    public required string Channel { get; set; }
    public string Status { get; set; } = "Draft";
    public decimal Budget { get; set; }
    public decimal ActualSpend { get; set; }
    public decimal AttributedRevenue { get; set; }
    public long Impressions { get; set; }
    public long Clicks { get; set; }
    public long Conversions { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
    public Brand? Brand { get; set; }
}

public sealed class SocialChannelConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? BrandId { get; set; }
    public required string Provider { get; set; }
    public required string DisplayName { get; set; }
    public string? ExternalAccountId { get; set; }
    public string? EncryptedAccessToken { get; set; }
    public string Status { get; set; } = "PendingAuthorization";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
    public Brand? Brand { get; set; }
}


public sealed class InboxConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ChannelConnectionId { get; set; }
    public Guid? ContactId { get; set; }
    public string? ExternalConversationId { get; set; }
    public string? Subject { get; set; }
    public string Status { get; set; } = "Open";
    public Guid? AssignedToUserId { get; set; }
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Organization? Organization { get; set; }
    public SocialChannelConnection? ChannelConnection { get; set; }
    public CrmContact? Contact { get; set; }
    public ICollection<InboxMessage> Messages { get; set; } = new List<InboxMessage>();
}

public sealed class InboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string? ExternalMessageId { get; set; }
    public string Direction { get; set; } = "Inbound";
    public required string Body { get; set; }
    public string Status { get; set; } = "Received";
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string? ProviderPayload { get; set; }
    public InboxConversation? Conversation { get; set; }
}


public sealed class ContentDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? BrandId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Channel { get; set; } = "General";
    public string Tone { get; set; } = "Professional";
    public string Status { get; set; } = "Draft";
    public string? Prompt { get; set; }
    public string? AiModel { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public int ApprovalRound { get; set; } = 1;
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishConnectionId { get; set; }
    public string? PublishTarget { get; set; }
    public string? PublishError { get; set; }
    public string? WhatsAppTemplateName { get; set; }
    public string? WhatsAppTemplateLanguage { get; set; }
    public string? WhatsAppTemplateParametersJson { get; set; }
    public ICollection<ContentDraftVersion> Versions { get; set; } = new List<ContentDraftVersion>();
}

public sealed class ContentDraftAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ContentDraftVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public int VersionNumber { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string ChangeNote { get; set; } = "ویرایش";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ContentDraft? Draft { get; set; }
}

public sealed class ContentApprovalRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftId { get; set; }
    public Guid OrganizationId { get; set; }
    public int ReviewRound { get; set; } = 1;
    public int StageNumber { get; set; }
    public string Decision { get; set; } = "Approved";
    public string? Comment { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class StatusDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Module { get; set; }
    public required string Key { get; set; }
    public required string Label { get; set; }
    public string Color { get; set; } = "#64748b";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class TextTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Module { get; set; }
    public required string Field { get; set; }
    public required string Name { get; set; }
    public required string Content { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Trigger { get; set; }
    public string ConditionField { get; set; } = "Any";
    public string ConditionOperator { get; set; } = "Equals";
    public string? ConditionValue { get; set; }
    public required string ActionType { get; set; }
    public string? ActivityType { get; set; }
    public required string ActionValue { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid RuleId { get; set; }
    public required string Trigger { get; set; }
    public required string SubjectType { get; set; }
    public Guid SubjectId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
    public string? EventKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}


public sealed class KnowledgeArticle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string Title { get; set; }
    public required string Slug { get; set; }
    public string? Summary { get; set; }
    public required string Body { get; set; }
    public string Category { get; set; } = "عمومی";
    public string Tags { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public int Version { get; set; } = 1;
    public Guid CreatedByUserId { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KnowledgeArticleRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArticleId { get; set; }
    public int Version { get; set; }
    public required string Title { get; set; }
    public required string Summary { get; set; }
    public required string Body { get; set; }
    public required string Category { get; set; }
    public required string Tags { get; set; }
    public string? ChangeNote { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}


public sealed class OrganizationAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public required string Summary { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OrganizationAiProviderSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public required string BaseUrl { get; set; }
    public required string Model { get; set; }
    public string? EncryptedApiKey { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SalesOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ContactId { get; set; }
    public required string OrderNumber { get; set; }
    public string Status { get; set; } = "Draft";
    public string Currency { get; set; } = "IRR";
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public int Version { get; set; } = 1;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SalesOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal LineTotal { get; set; }
    public int SortOrder { get; set; }
}

public sealed class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public required string Method { get; set; }
    public required string Status { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
