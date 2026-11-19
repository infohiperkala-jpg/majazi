using Majazi.Api.Data;
using Majazi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Majazi.Api;

public static class DemoDataSeeder
{
    private const string Marker = "[DEMO:DIGIBAIL]";
    private static readonly string[] Names =
    [
        "آرمان رضایی", "نگار محمدی", "پارسا کریمی", "سارا احمدی", "امیرحسین موسوی",
        "مهسا جعفری", "علی اکبری", "ترانه حسینی", "محمد صادقی", "الناز مرادی"
    ];
    private static readonly string[] Products =
    [
        "آیفون ۱۶ پرو مکس", "سامسونگ Galaxy S25 Ultra", "شیائومی 14T Pro", "آیفون ۱۵",
        "Samsung A56", "هدفون Sony WH-1000XM5", "ساعت Galaxy Watch 7", "AirPods Pro 2",
        "پاوربانک Anker 20000", "شارژر ۴۵ وات سامسونگ"
    ];
    private static readonly string[] Channels =
    ["Instagram", "Telegram", "Website", "Instagram", "Telegram", "Website", "Email", "Instagram", "Telegram", "Website"];

    public static async Task<object> SeedAsync(MajaziDbContext db, Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.Slug == "digibail-demo", ct);
        if (org is null)
        {
            org = new Organization { Name = "دیجی‌بایل | فروشگاه تخصصی موبایل و کالای دیجیتال", Slug = "digibail-demo" };
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(new OrganizationMembership { UserId = userId, OrganizationId = org.Id, Role = "Owner" });
        }
        else
        {
            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x => x.OrganizationId == org.Id && x.UserId == userId, ct);
            if (membership is null)
                throw new InvalidOperationException("سازمان نمونه از قبل وجود دارد و متعلق به فضای کاری دیگری است.");
            if (membership.Role is not ("Owner" or "Admin"))
                throw new UnauthorizedAccessException("برای آماده‌سازی داده نمونه دسترسی مدیر لازم است.");
        }
        await db.SaveChangesAsync(ct);

        var brand = await db.Brands.SingleOrDefaultAsync(x => x.OrganizationId == org.Id && x.Slug == "digibail", ct);
        if (brand is null)
        {
            brand = new Brand { OrganizationId = org.Id, Name = "دیجی‌بایل", Slug = "digibail" };
            db.Brands.Add(brand);
            await db.SaveChangesAsync(ct);
        }

        var contacts = await db.CrmContacts.Where(x => x.OrganizationId == org.Id && x.Notes != null && x.Notes.Contains(Marker)).ToListAsync(ct);
        if (contacts.Count == 0)
        {
            for (var i = 0; i < 10; i++)
                contacts.Add(new CrmContact
                {
                    OrganizationId = org.Id, BrandId = brand.Id, FullName = Names[i],
                    Email = $"customer{i + 1:00}@example.com", Phone = $"+989000000{i + 1:00}",
                    Company = i % 3 == 0 ? "خریدار سازمانی" : "مشتری فروشگاه",
                    Notes = $"{Marker} مشتری نمونه علاقه‌مند به {Products[i]}؛ داده آزمایشی و غیرواقعی."
                });
            db.CrmContacts.AddRange(contacts);
            await db.SaveChangesAsync(ct);
        }

        var leads = await db.CrmLeads.Where(x => x.OrganizationId == org.Id && x.Notes != null && x.Notes.Contains(Marker)).ToListAsync(ct);
        if (leads.Count == 0)
        {
            var stages = new[] { "New", "Qualified", "Proposal", "Negotiation", "Won", "New", "Qualified", "Proposal", "Negotiation", "Lost" };
            for (var i = 0; i < 10; i++)
                leads.Add(new CrmLead
                {
                    OrganizationId = org.Id, BrandId = brand.Id, ContactId = contacts[i % contacts.Count].Id,
                    Title = $"درخواست خرید {Products[i]}", Stage = stages[i],
                    EstimatedValue = 8_000_000m + (i * 3_750_000m),
                    Source = new[] { "Instagram", "Telegram", "Website", "Referral", "Walk-in" }[i % 5],
                    Notes = $"{Marker} فرصت فروش آزمایشی؛ مبلغ و مرحله صرفاً نمونه هستند."
                });
            db.CrmLeads.AddRange(leads);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.CrmActivities.AnyAsync(x => x.OrganizationId == org.Id && x.Summary.Contains(Marker), ct))
        {
            for (var i = 0; i < 10; i++)
                db.CrmActivities.Add(new CrmActivity
                {
                    OrganizationId = org.Id, ContactId = contacts[i % contacts.Count].Id,
                    LeadId = leads[i % leads.Count].Id, CreatedByUserId = userId,
                    ActivityType = new[] { "Call", "Note", "Email", "Meeting" }[i % 4],
                    Summary = $"{Marker} پیگیری نمونه {Products[i]}؛ {Names[i]} برای دریافت پیشنهاد قیمت تماس گرفته است.",
                    OccurredAt = DateTimeOffset.UtcNow.AddDays(-i)
                });
        }

        if (!await db.MarketingCampaigns.AnyAsync(x => x.OrganizationId == org.Id && x.Notes != null && x.Notes.Contains(Marker), ct))
        {
            var campaignNames = new[]
            {
                "کمپین معرفی پرچمداران اپل", "فروش ویژه گوشی‌های سامسونگ", "جشنواره شیائومی",
                "کمپین لوازم جانبی اصل", "بازگشت مشتریان قدیمی", "پیشنهاد خرید سازمانی",
                "کمپین محتوای آموزشی موبایل", "فروش ویژه هدفون و ساعت هوشمند",
                "کمپین پیامکی موجودی جدید", "جمعه دیجیتال دیجی‌بایل"
            };
            for (var i = 0; i < 10; i++)
            {
                var impressions = 12000L + i * 2450L;
                var clicks = 600L + i * 105L;
                var conversions = 24L + i * 5L;
                db.MarketingCampaigns.Add(new MarketingCampaign
                {
                    OrganizationId = org.Id, BrandId = brand.Id, Name = campaignNames[i],
                    Objective = $"افزایش فروش و آگاهی از {Products[i]}",
                    Channel = new[] { "Instagram", "Telegram", "Website", "Email", "Ads" }[i % 5],
                    Status = new[] { "Active", "Completed", "Scheduled", "Paused", "Draft" }[i % 5],
                    Budget = 25_000_000m + i * 5_000_000m,
                    ActualSpend = 8_000_000m + i * 1_250_000m,
                    AttributedRevenue = 45_000_000m + i * 9_000_000m,
                    Impressions = impressions, Clicks = clicks, Conversions = conversions,
                    StartsAt = DateTimeOffset.UtcNow.AddDays(-i * 2),
                    EndsAt = DateTimeOffset.UtcNow.AddDays(14 + i),
                    Notes = $"{Marker} شاخص‌های عملکرد ساختگی برای نمایش داشبورد؛ داده واقعی تبلیغات نیست."
                });
            }
        }

        if (!await db.ContentDrafts.AnyAsync(x => x.OrganizationId == org.Id && x.Prompt == Marker, ct))
        {
            var posts = new[]
            {
                ("راهنمای خرید آیفون؛ قبل از انتخاب بخوانید", "برای انتخاب آیفون مناسب، ظرفیت حافظه، سلامت باتری، گارانتی معتبر و نیاز واقعی خود را بررسی کنید. در دیجی‌بایل، مشخصات و مشاوره خرید را شفاف دریافت کنید. #دیجی_بایل #آیفون"),
                ("Galaxy S25 Ultra؛ قدرت در دستان شما", "اگر دوربین حرفه‌ای، نمایشگر بزرگ و عملکرد سریع می‌خواهید، Galaxy S25 Ultra را با نیاز و بودجه خود مقایسه کنید. برای استعلام موجودی و شرایط خرید پیام بدهید. #سامسونگ"),
                ("۵ نکته برای نگهداری باتری موبایل", "از گرمای شدید دوری کنید، شارژر استاندارد به‌کار ببرید، به‌روزرسانی‌ها را نصب کنید و سلامت باتری را دوره‌ای بررسی کنید. مراقبت درست، عمر دستگاه را بیشتر می‌کند."),
                ("لوازم جانبی اصل را چطور تشخیص دهیم؟", "به بسته‌بندی، شماره مدل، کیفیت ساخت، مشخصات فنی و ضمانت فروشنده توجه کنید. قیمت غیرعادی پایین همیشه نشانه یک خرید خوب نیست."),
                ("شیائومی 14T Pro مناسب چه کسانی است؟", "پیش از خرید، دوربین، نمایشگر، پردازنده، حافظه و خدمات پس از فروش را کنار هم بسنجید. انتخاب درست یعنی محصول متناسب با استفاده روزانه شما."),
                ("پیشنهاد ویژه هدفون‌های بی‌سیم", "برای انتخاب هدفون، کیفیت مکالمه، حذف نویز، راحتی، دوام باتری و سازگاری با گوشی را بررسی کنید. مدل مناسب را بر اساس سبک استفاده‌تان انتخاب کنید."),
                ("راهنمای انتخاب پاوربانک", "ظرفیت واقعی، توان خروجی، تعداد درگاه‌ها، استانداردهای ایمنی و وزن را بررسی کنید. برای گوشی‌های جدید، توان شارژ سازگار اهمیت زیادی دارد."),
                ("ساعت هوشمند؛ فراتر از نمایش زمان", "اعلان‌ها، سازگاری با موبایل، عمر باتری، کیفیت نمایشگر و امکانات ورزشی را مقایسه کنید تا انتخابی کاربردی داشته باشید."),
                ("چک‌لیست خرید گوشی کارکرده", "شناسه دستگاه، سلامت نمایشگر، دوربین، باتری، درگاه شارژ، سابقه تعمیر و اصالت کالا را بررسی کنید و خرید را با فاکتور معتبر انجام دهید."),
                ("دیجی‌بایل؛ انتخاب آگاهانه کالای دیجیتال", "هدف ما کمک به انتخاب آگاهانه است: توضیح روشن مشخصات، مقایسه منصفانه و پاسخ‌گویی پیش از خرید. برای مشاوره محصول موردنظرتان با ما در ارتباط باشید.")
            };
            for (var i = 0; i < posts.Length; i++)
            {
                var draft = new ContentDraft
                {
                    OrganizationId = org.Id, BrandId = brand.Id, CreatedByUserId = userId,
                    Title = posts[i].Item1, Content = posts[i].Item2,
                    Channel = Channels[i], Tone = "Professional", Status = "Draft",
                    Prompt = Marker
                };
                draft.Versions.Add(new ContentDraftVersion
                {
                    DraftId = draft.Id, VersionNumber = 1, Title = draft.Title, Content = draft.Content,
                    ChangeNote = "نسخه اولیه داده نمونه", CreatedByUserId = userId
                });
                db.ContentDrafts.Add(draft);
            }
        }

        if (!await db.InboxConversations.AnyAsync(x => x.OrganizationId == org.Id && x.Subject != null && x.Subject.Contains(Marker), ct))
        {
            for (var i = 0; i < 10; i++)
            {
                var conversation = new InboxConversation
                {
                    OrganizationId = org.Id, ContactId = contacts[i % contacts.Count].Id,
                    Subject = $"{Marker} گفت‌وگوی نمونه درباره {Products[i]}",
                    Status = new[] { "Open", "Open", "Pending", "Resolved", "Open" }[i % 5],
                    LastMessageAt = DateTimeOffset.UtcNow.AddHours(-i)
                };
                conversation.Messages.Add(new InboxMessage
                {
                    Direction = "Inbound", Body = $"سلام، درباره {Products[i]} لطفاً قیمت و شرایط گارانتی را اعلام می‌کنید؟",
                    Status = "Received", CreatedAt = DateTimeOffset.UtcNow.AddHours(-i).AddMinutes(-10)
                });
                conversation.Messages.Add(new InboxMessage
                {
                    Direction = "Outbound", Body = "سلام و وقت بخیر. با کمال میل؛ همکاران دیجی‌بایل مشخصات، موجودی و شرایط ضمانت را برای شما ارسال می‌کنند.",
                    Status = "Demo", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow.AddHours(-i)
                });
                db.InboxConversations.Add(conversation);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new
        {
            organizationId = org.Id, organization = org.Name, brandId = brand.Id, brand = brand.Name,
            demo = true, marker = Marker,
            counts = new
            {
                contacts = await db.CrmContacts.CountAsync(x => x.OrganizationId == org.Id && x.Notes != null && x.Notes.Contains(Marker), ct),
                leads = await db.CrmLeads.CountAsync(x => x.OrganizationId == org.Id && x.Notes != null && x.Notes.Contains(Marker), ct),
                activities = await db.CrmActivities.CountAsync(x => x.OrganizationId == org.Id && x.Summary.Contains(Marker), ct),
                campaigns = await db.MarketingCampaigns.CountAsync(x => x.OrganizationId == org.Id && x.Notes != null && x.Notes.Contains(Marker), ct),
                contentDrafts = await db.ContentDrafts.CountAsync(x => x.OrganizationId == org.Id && x.Prompt == Marker, ct),
                inboxConversations = await db.InboxConversations.CountAsync(x => x.OrganizationId == org.Id && x.Subject != null && x.Subject.Contains(Marker), ct)
            },
            note = "همه داده‌ها آزمایشی هستند؛ هیچ کانال اجتماعی به‌صورت جعلی متصل نشده است."
        };
    }
}