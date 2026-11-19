"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import "./channels.css";

type Organization = { id: string; name: string; role: string };
type Brand = { id: string; name: string };
type Channel = { id: string; brandId?: string | null; provider: string; displayName: string; externalAccountId?: string | null; status: string; hasCredential: boolean; createdAt: string; updatedAt?: string };
const providers = [{ id: "Instagram", label: "اینستاگرام" }, { id: "Facebook", label: "Facebook Messenger" }, { id: "Telegram", label: "تلگرام" }, { id: "WhatsApp", label: "واتساپ بیزینس" }, { id: "Bale", label: "بله (ربات رسمی)" }, { id: "Rubika", label: "روبیکا (ربات رسمی)" }];
const statusLabel: Record<string, string> = { PendingAuthorization: "در انتظار مجوز رسمی", CredentialsStored: "اعتبارنامه ذخیره شده؛ اتصال تأیید نشده", Connected: "متصل و تأییدشده", VerificationFailed: "اعتبارسنجی ناموفق" };
async function request<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const response = await fetch("/backend-api" + path, { ...init, headers: { "Content-Type": "application/json", Authorization: "Bearer " + token, ...init?.headers }, cache: "no-store" });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? "عملیات انجام نشد.");
  return data as T;
}
export default function ChannelsPage() {
  const [token, setToken] = useState("");
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [brands, setBrands] = useState<Brand[]>([]);
  const [items, setItems] = useState<Channel[]>([]);
  const [provider, setProvider] = useState("Instagram");
  const [displayName, setDisplayName] = useState("");
  const [externalAccountId, setExternalAccountId] = useState("");
  const [brandId, setBrandId] = useState("");
  const [accessToken, setAccessToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [metaReady, setMetaReady] = useState(false);
  async function reload(org = organizationId, access = token) {
    if (!org || !access) return;
    const [channels, brandList, metaConfig] = await Promise.all([
      request<Channel[]>("/api/organizations/" + org + "/channels", access),
      request<Brand[]>("/api/organizations/" + org + "/brands", access),
      request<{ instagramBusinessLoginReady: boolean }>("/api/organizations/" + org + "/channels/meta/configuration", access)
    ]);
    setItems(channels); setBrands(brandList); setMetaReady(metaConfig.instagramBusinessLoginReady);
  }
  useEffect(() => {
    const saved = localStorage.getItem("majazi_access_token") ?? "";
    setToken(saved);
    if (saved) request<Organization[]>("/api/organizations", saved).then(list => {
      setOrganizations(list); setOrganizationId(list[0]?.id ?? "");
    }).catch(e => setError(e instanceof Error ? e.message : "دریافت سازمان‌ها ناموفق بود."));
  }, []);
  useEffect(() => {
    if (organizationId && token) void reload(organizationId, token).catch(e => setError(e instanceof Error ? e.message : "بارگذاری کانال‌ها ناموفق بود."));
  }, [organizationId, token]);
  async function run(action: () => Promise<void>) {
    setBusy(true); setError(""); setNotice("");
    try { await action(); } catch (e) { setError(e instanceof Error ? e.message : "عملیات ناموفق بود."); }
    finally { setBusy(false); }
  }
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/channels", token, {
        method: "POST",
        body: JSON.stringify({ provider, displayName, externalAccountId: externalAccountId || null, brandId: brandId || null, accessToken: accessToken || null })
      });
      setDisplayName(""); setExternalAccountId(""); setBrandId(""); setAccessToken("");
      await reload(); setNotice("اطلاعات کانال به‌صورت امن ثبت شد. وضعیت اتصال تا زمان آزمون واقعی API تأییدشده محسوب نمی‌شود.");
    });
  }
  async function connectInstagram() {
    await run(async () => {
      const result = await request<{ authorizationUrl: string }>("/api/organizations/" + organizationId + "/channels/meta/connect", token, { method: "GET" });
      window.location.assign(result.authorizationUrl);
    });
  }
  async function healthCheck(item: Channel) {
    await run(async () => {
      const result = await request<{ healthy: boolean; accountName?: string; latencyMs?: number; message: string }>("/api/organizations/" + organizationId + "/channels/" + item.id + "/health-check", token, { method: "POST" });
      await reload(); setNotice((result.healthy ? "اتصال سالم است. " : "بررسی اتصال ناموفق بود. ") + result.message + (result.accountName ? " حساب: " + result.accountName : "") + (result.latencyMs != null ? " · زمان پاسخ: " + result.latencyMs + "ms" : ""));
    });
  }
  async function verify(item: Channel) {
    await run(async () => {
      const result = await request<{ username?: string; status: string }>("/api/organizations/" + organizationId + "/channels/" + item.id + "/verify", token, { method: "POST" });
      await reload(); setNotice("ربات " + item.provider + " تأیید شد." + (result.username ? " نام کاربری: @" + result.username : ""));
    });
  }
  async function configureBotWebhook(item: Channel) {
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/channels/" + item.id + "/bot-webhook", token, { method: "POST" });
      setNotice("وب‌هوک امن " + item.provider + " ثبت شد؛ پیام‌های ورودی به صندوق یکپارچه منتقل می‌شوند.");
    });
  }
  async function configureMetaWebhook(item: Channel) {
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/channels/" + item.id + "/meta-webhook", token, { method: "POST" });
      setNotice("اشتراک وب‌هوک پیام‌های " + item.provider + " در Meta ثبت شد. دریافت پیام‌ها به Inbox منتقل می‌شود.");
    });
  }
  async function configureTelegramWebhook(item: Channel) {
    await run(async () => {
      const result = await request<{ configured: boolean; webhookUrl: string }>(
        "/api/organizations/" + organizationId + "/channels/" + item.id + "/webhook",
        token,
        { method: "POST" }
      );
      setNotice("وب‌هوک تلگرام با موفقیت ثبت شد. دریافت پیام‌های جدید این ربات به صندوق ورودی متصل شد.");
    });
  }
  async function remove(item: Channel) {
    if (!window.confirm("اتصال ثبت‌شده «" + item.displayName + "» حذف شود؟")) return;
    await run(async () => { await request("/api/organizations/" + organizationId + "/channels/" + item.id, token, { method: "DELETE" }); await reload(); setNotice("اطلاعات اتصال حذف شد."); });
  }
  if (!token) return <main className="channels-shell"><section className="channels-panel"><h1>ورود به مدیریت کانال‌ها</h1><p>ابتدا از داشبورد اصلی وارد حساب کاربری شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;
  return <main className="channels-shell" dir="rtl">
    <header className="channels-header"><div><span>MAJAZI / CHANNELS</span><h1>اتصال شبکه‌های اجتماعی</h1><p>مدیریت کانال‌های هر سازمان و برند</p></div><Link href="/">بازگشت به داشبورد</Link></header>
    <section className="channels-toolbar"><label>فضای سازمانی<select value={organizationId} onChange={e => setOrganizationId(e.target.value)}>{organizations.map(o => <option key={o.id} value={o.id}>{o.name} · {o.role}</option>)}</select></label><div className="channels-actions"><button type="button" disabled={busy || !organizationId || !metaReady} onClick={() => void connectInstagram()}>اتصال رسمی اینستاگرام با Meta</button><button type="button" disabled={busy || !organizationId} onClick={() => void run(async () => { await reload(); setNotice("فهرست کانال‌ها به‌روزرسانی شد."); })}>به‌روزرسانی</button></div></section>
    {error && <div className="channels-error" role="alert">{error}</div>}{notice && <div className="channels-notice" role="status">{notice}</div>}
    <section className="channels-metrics">
      <article><span>تمام کانال‌ها</span><strong>{items.length.toLocaleString("en-US")}</strong><small>ثبت‌شده در این سازمان</small></article>
      <article><span>اتصال‌های تأییدشده</span><strong>{items.filter(x => x.status === "Connected").length.toLocaleString("en-US")}</strong><small>تأییدشده توسط سرویس مقصد</small></article>
      <article><span>نیازمند اقدام</span><strong>{items.filter(x => x.status !== "Connected").length.toLocaleString("en-US")}</strong><small>در انتظار اعتبارسنجی یا مجوز</small></article>
      <article><span>اتصال رسمی اینستاگرام</span><strong className={metaReady ? "metric-ready" : "metric-pending"}>{metaReady ? "آماده" : "نیازمند تنظیمات"}</strong><small>وضعیت پیکربندی OAuth سرور</small></article>
    </section>
    <section className="channels-warning"><strong>نکته امنیتی و وضعیت اتصال</strong><p>توکن‌ها در پایگاه داده رمزنگاری می‌شوند و در پاسخ API نمایش داده نمی‌شوند. برای واتساپ باید Phone Number ID را در شناسه حساب و System User Access Token را در فیلد توکن وارد کنید؛ سپس آزمون زنده اتصال را اجرا کنید. ارسال پاسخ از صندوق ورودی از WhatsApp Cloud API انجام می‌شود. اتصال رسمی اینستاگرام از Business Login انجام می‌شود؛ دکمه اتصال زمانی فعال است که App ID، App Secret و Redirect URI امن روی سرور تنظیم شده باشند. پس از تأیید ربات تلگرام، با دکمه «فعال‌سازی دریافت پیام» وب‌هوک امن را ثبت کنید. برای این کار مقدار PUBLIC_BASE_URL را در فایل .env روی دامنه عمومی HTTPS تنظیم و سرویس API را بازراه‌اندازی کنید. دریافت پیام‌های ورودی تلگرام از این مسیر به صندوق ورودی منتقل می‌شود. برای Facebook Messenger شناسه صفحه و Page Access Token دارای مجوز pages_messaging را ثبت و آزمون کنید. برای Instagram دسترسی instagram_business_manage_messages را در Business Login تأیید کنید. سپس برای هر حساب متصل دکمه فعال‌سازی وب‌هوک Meta را بزنید. وب‌هوک مشترک Meta برای Instagram و Facebook از آدرس /api/webhooks/meta استفاده می‌کند. برای بله و روبیکا، توکن ربات رسمی را ثبت کنید، آزمون زنده و تأیید ربات را انجام دهید و سپس وب‌هوک را فعال کنید؛ آدرس عمومی HTTPS باید در PUBLIC_BASE_URL تنظیم شده باشد. شناسه گفتگوی مخاطب از رویداد ورودی ثبت می‌شود. برای دریافت پیام‌ها و وضعیت تحویل واتساپ، در تنظیمات سرور Meta__AppSecret و Meta__WebhookVerifyToken را تعریف کنید و Callback URL را روی https://دامنه-شما/api/webhooks/whatsapp با همان Verify Token ثبت کنید؛ سپس فیلد messages را برای WhatsApp Business Account مشترک کنید.</p></section>
    <section className="channels-panel"><h2>ثبت کانال</h2><form className="channels-form" onSubmit={submit}>
      <label>پلتفرم<select value={provider} onChange={e => setProvider(e.target.value)}>{providers.map(x => <option key={x.id} value={x.id}>{x.label}</option>)}</select></label>
      <label>نام نمایشی<input required maxLength={180} value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="مثلاً صفحه رسمی برند"/></label>
      <label>شناسه حساب / کانال<input maxLength={180} value={externalAccountId} onChange={e => setExternalAccountId(e.target.value)} placeholder="شناسه عددی یا نام کاربری"/></label>
      <label>برند مرتبط<select value={brandId} onChange={e => setBrandId(e.target.value)}><option value="">بدون برند مشخص</option>{brands.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
      <label className="channels-wide">توکن دسترسی (اختیاری)<input type="password" autoComplete="new-password" maxLength={8000} value={accessToken} onChange={e => setAccessToken(e.target.value)} placeholder="فقط در صورت داشتن اعتبارنامه رسمی وارد کنید"/></label>
      <div className="channels-wide channels-actions"><button disabled={busy || !organizationId}>ثبت امن کانال</button></div>
    </form></section>
    <section className="channels-panel"><div className="channels-heading"><h2>کانال‌های ثبت‌شده</h2><span>{items.length.toLocaleString("en-US")} مورد</span></div>
      {items.length === 0 ? <p className="channels-empty">هنوز کانالی برای این سازمان ثبت نشده است.</p> : <div className="channels-list">{items.map(item => <article className="channel-card" key={item.id}>
        <div className="channel-mark">{item.provider === "Telegram" ? "TG" : item.provider === "Facebook" ? "FB" : item.provider === "WhatsApp" ? "WA" : item.provider === "Bale" ? "BL" : item.provider === "Rubika" ? "RB" : "IG"}</div>
        <div className="channel-info"><strong>{item.displayName}</strong><span>{providers.find(x => x.id === item.provider)?.label ?? item.provider}{item.externalAccountId ? " · " + item.externalAccountId : ""}</span><small>{statusLabel[item.status] ?? item.status}{item.hasCredential ? " · دارای اعتبارنامه رمز‌شده" : " · بدون اعتبارنامه"}</small></div>
        <div className="channel-actions">{item.hasCredential && (item.provider === "Telegram" || item.provider === "Instagram" || item.provider === "Facebook" || item.provider === "WhatsApp" || item.provider === "Bale" || item.provider === "Rubika") && <button type="button" className="channels-verify" disabled={busy} onClick={() => void healthCheck(item)}>آزمون زنده اتصال</button>}{(item.provider === "Telegram" || item.provider === "Bale" || item.provider === "Rubika") && item.hasCredential && <button type="button" className="channels-verify" disabled={busy} onClick={() => void verify(item)}>تأیید ربات</button>}{(item.provider === "Bale" || item.provider === "Rubika") && item.status === "Connected" && <button type="button" className="channels-verify" disabled={busy} onClick={() => void configureBotWebhook(item)}>فعال‌سازی دریافت پیام</button>}{item.provider === "Telegram" && item.status === "Connected" && <button type="button" className="channels-verify" disabled={busy} onClick={() => void configureTelegramWebhook(item)}>فعال‌سازی دریافت پیام</button>}{(item.provider === "Instagram" || item.provider === "Facebook") && item.status === "Connected" && <button type="button" className="channels-verify" disabled={busy} onClick={() => void configureMetaWebhook(item)}>فعال‌سازی وب‌هوک Meta</button>}<button type="button" className="channels-delete" disabled={busy} onClick={() => void remove(item)}>حذف</button></div>
      </article>)}</div>}
    </section>
    <footer>MAJAZI CHANNELS · داده‌ها در محدوده سازمان انتخاب‌شده نگهداری می‌شوند.</footer>
  </main>;
}
