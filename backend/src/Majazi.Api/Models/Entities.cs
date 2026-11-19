namespace Majazi.Api.Models;

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
    public ICollection<ContentDraftVersion> Versions { get; set; } = new List<ContentDraftVersion>();
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
