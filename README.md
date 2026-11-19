# MAJAZI — مرکز هوشمند بازاریابی و فروش

سامانه چندسازمانی و چندبرندی برای مدیریت بازاریابی، محتوا، ارتباط با مشتری و فروش.

## ساختار
- `backend/`: API مبتنی بر ASP.NET Core 10 و PostgreSQL
- `apps/admin-web/`: پنل مدیریتی Next.js و TypeScript
- `docs/`: معماری و وضعیت توسعه

## اجرای محلی
1. `cp .env.example .env`
2. در فایل `.env` مقدار `JWT_SIGNING_KEY` را با یک مقدار تصادفی محرمانه و حداقل ۳۲ بایت جایگزین کنید؛ این کلید را در Git ثبت نکنید.
3. `docker compose up --build`
4. پنل: http://localhost:3000
5. API: http://localhost:5080
6. سلامت API: http://localhost:5080/health

برای سرویس‌های خارجی، مسیر خروجی HTTP(S) جداگانه و اختیاری قابل تنظیم است: `NETWORK_INTERNATIONAL_DEFAULT_PROXY` یا پراکسی اختصاصی `NETWORK_TELEGRAM_PROXY`، `NETWORK_META_PROXY` و `NETWORK_AI_PROXY`. این تنظیم فقط به کلاینت‌های خارجی مربوط است؛ سرویس‌های ایرانی باید با مسیر مستقیم و بدون پراکسی خارجی پیاده‌سازی شوند. این قابلیت VPN نصب نمی‌کند و به پراکسی/VPN قابل‌دسترسی از داخل کانتینر API نیاز دارد. اطلاعات محرمانه پراکسی را در `.env` سرور نگه دارید و در Git یا گفتگو ارسال نکنید.\n\nAPI هنگام راه‌اندازی، Migrationهای EF Core را روی PostgreSQL اعمال می‌کند. ثبت‌نام اولیه با ایمیل، گذرواژه قوی و نام سازمان انجام می‌شود؛ کاربر ثبت‌نام‌کننده مالک سازمان خواهد بود. ورود با ایمیل و گذرواژه، JWT یک‌ساعته صادر می‌کند. دسترسی به اطلاعات سازمان و CRM بر اساس عضویت همان سازمان کنترل می‌شود.

## Iranian bot channels: Bale and Rubika
- **Bale:** create a bot with Bale BotFather and store its bot token as the channel credential. The adapter uses the Bale Bot API `getMe`, `sendMessage`, and `setWebhook` methods at `https://tapi.bale.ai/bot{TOKEN}/{METHOD}`.
- **Rubika:** create a bot using the Rubika bot platform and store its bot token as the channel credential. The adapter uses the Rubika Bot API `getMe`, `sendMessage`, and `updateBotEndpoints` methods at `https://botapi.rubika.ir/v3/{TOKEN}/{METHOD}`; Rubika responses use the `status/data` envelope.
- In Channels, add the bot, run **Live connection test**, **Verify bot**, then **Activate incoming messages**. The API server must have `PublicBaseUrl` set to the public HTTPS origin. Webhook URLs are signed per connection and are not shared between organizations.
- Incoming text messages create Inbox conversations and trigger the existing `InboxMessageReceived` automation. Duplicate provider message IDs are ignored. Text replies are sent through each provider's Bot API.
- The first adapter scope is text messaging. Media upload/download, channel-wide broadcasts, and provider-specific advanced controls are not implied by this connection.


## Instagram Messaging and Facebook Messenger
- Instagram Business Login requests `instagram_business_manage_messages` in addition to the existing account/content scopes. The account must be eligible for Instagram Messaging and the Meta app must have the required access/review for production users.
- Facebook Messenger is connected by registering the Facebook Page ID and its Page Access Token with `pages_messaging` permission. Use a Page token for a Page where the app has messaging task access; do not use a user token.
- Shared callback: `https://YOUR_PUBLIC_DOMAIN/api/webhooks/meta`; configure the Meta Webhooks product for the `instagram` and `page` objects and subscribe to `messages`. Verify token and App Secret are the same server-side values used for WhatsApp webhooks.
- After connecting each Instagram account or Facebook Page, use its **Activate Meta webhook** action in Channels to subscribe that account/page to message events. Replies are sent through the corresponding official Meta Send API. Messaging windows, app review, recipient consent, and Meta platform policies still apply.


## WhatsApp Business webhook
برای دریافت رویدادهای WhatsApp Cloud API، دامنه عمومی باید HTTPS و در دسترس Meta باشد. این متغیرها را فقط در Secretهای محیط اجرای API (نه در Git و نه در مرورگر) تنظیم کنید:

- `Meta__AppSecret`: مقدار App Secret اپلیکیشن Meta؛ برای اعتبارسنجی امضای `X-Hub-Signature-256` تمام درخواست‌های POST استفاده می‌شود.
- `Meta__WebhookVerifyToken`: یک رشته تصادفی محرمانه که هنگام Verify کردن Callback URL در Meta Developer Dashboard وارد می‌کنید.
- `Meta__GraphApiVersion`: نسخه Graph API مورد استفاده آداپتور خروجی؛ پیش‌فرض فعلی `v22.0` است.

Callback URL برابر `https://YOUR_PUBLIC_DOMAIN/api/webhooks/whatsapp` است. در Meta Webhooks، شیء `whatsapp_business_account` و فیلد `messages` را مشترک کنید. این مسیر GET challenge را فقط با Verify Token درست تأیید می‌کند و POST را فقط با HMAC-SHA256 معتبر می‌پذیرد. پیام‌های ورودی متنی و انواع رسانه‌ای به صندوق ورودی سازمان متصل به Phone Number ID اضافه می‌شوند؛ رویدادهای sent، delivered، read و failed نیز وضعیت پیام خروجی متناظر را به‌روزرسانی می‌کنند. رویدادهای تکراری پیام با شناسه پیام خارجی نادیده گرفته می‌شوند.


## CRM
مسیر `/crm` شامل مدیریت مخاطبان، ایجاد، ویرایش و حذف فرصت‌های فروش، تغییر مرحله قیف فروش و ثبت تاریخچه تماس، ایمیل، جلسه، یادداشت و وظیفه با اتصال به مخاطب و فرصت مرتبط است. داده‌های CRM با شناسه سازمان محدود می‌شوند و اتصال برند یا مخاطب به سازمان دیگر در API رد می‌شود.

## وضعیت و محدودیت‌ها
این پروژه در مرحله MVP است. Buildهای GitHub Actions وضعیت کامپایل و ساخت وب را بررسی می‌کنند؛ اجرای Docker و تست Integration با PostgreSQL باید جداگانه انجام شوند. پیش از انتشار عمومی، آزمون‌های خودکار مجوزدهی و جداسازی tenant، مدیریت نشست و ابطال توکن، بازیابی گذرواژه، تأیید ایمیل، محدودسازی نرخ مبتنی بر IP و hardening عملیاتی تکمیل شوند. توکن فعلی در مرورگر نگهداری می‌شود؛ برای محیط عمومی، انتقال به cookie امن HttpOnly توصیه می‌شود.
