"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import "./channels.css";

type Organization = { id: string; name: string; role: string };
type Brand = { id: string; name: string };
type Channel = { id: string; brandId?: string | null; provider: string; displayName: string; externalAccountId?: string | null; status: string; hasCredential: boolean; createdAt: string };
const providers = [{ id: "Instagram", label: "اینستاگرام" }, { id: "Facebook", label: "فیسبوک" }, { id: "Telegram", label: "تلگرام" }];
const statusLabel: Record<string, string> = { PendingAuthorization: "در انتظار مجوز رسمی", CredentialsStored: "اعتبارنامه ذخیره شده؛ اتصال تأیید نشده" };
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
  async function reload(org = organizationId, access = token) {
    if (!org || !access) return;
    const [channels, brandList] = await Promise.all([
      request<Channel[]>("/api/organizations/" + org + "/channels", access),
      request<Brand[]>("/api/organizations/" + org + "/brands", access)
    ]);
    setItems(channels); setBrands(brandList);
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
      await reload(); setNotice("اطلاعات کانال به‌صورت امن ثبت شد. وضعیت اتصال باید پس از OAuth یا اعتبارسنجی رسمی تأیید شود.");
    });
  }
  async function remove(item: Channel) {
    if (!window.confirm("اتصال ثبت‌شده «" + item.displayName + "» حذف شود؟")) return;
    await run(async () => { await request("/api/organizations/" + organizationId + "/channels/" + item.id, token, { method: "DELETE" }); await reload(); setNotice("اطلاعات اتصال حذف شد."); });
  }
  if (!token) return <main className="channels-shell"><section className="channels-panel"><h1>ورود به مدیریت کانال‌ها</h1><p>ابتدا از داشبورد اصلی وارد حساب کاربری شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;
  return <main className="channels-shell" dir="rtl">
    <header className="channels-header"><div><span>MAJAZI / CHANNELS</span><h1>اتصال شبکه‌های اجتماعی</h1><p>مدیریت کانال‌های هر سازمان و برند</p></div><Link href="/">بازگشت به داشبورد</Link></header>
    <section className="channels-toolbar"><label>فضای سازمانی<select value={organizationId} onChange={e => setOrganizationId(e.target.value)}>{organizations.map(o => <option key={o.id} value={o.id}>{o.name} · {o.role}</option>)}</select></label><button type="button" disabled={busy || !organizationId} onClick={() => void run(async () => { await reload(); setNotice("فهرست کانال‌ها به‌روزرسانی شد."); })}>به‌روزرسانی</button></section>
    {error && <div className="channels-error" role="alert">{error}</div>}{notice && <div className="channels-notice" role="status">{notice}</div>}
    <section className="channels-warning"><strong>نکته امنیتی و وضعیت اتصال</strong><p>توکن ارسالی در پایگاه داده رمزنگاری می‌شود و هیچ‌گاه در پاسخ API نمایش داده نمی‌شود. ثبت توکن به‌تنهایی به معنی اتصال تأییدشده نیست. اتصال عملیاتی اینستاگرام و فیسبوک به OAuth و مجوزهای برنامه Meta و تلگرام به اعتبارسنجی Bot API نیاز دارد.</p></section>
    <section className="channels-panel"><h2>ثبت کانال</h2><form className="channels-form" onSubmit={submit}>
      <label>پلتفرم<select value={provider} onChange={e => setProvider(e.target.value)}>{providers.map(x => <option key={x.id} value={x.id}>{x.label}</option>)}</select></label>
      <label>نام نمایشی<input required maxLength={180} value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="مثلاً صفحه رسمی برند"/></label>
      <label>شناسه حساب / کانال<input maxLength={180} value={externalAccountId} onChange={e => setExternalAccountId(e.target.value)} placeholder="شناسه عددی یا نام کاربری"/></label>
      <label>برند مرتبط<select value={brandId} onChange={e => setBrandId(e.target.value)}><option value="">بدون برند مشخص</option>{brands.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
      <label className="channels-wide">توکن دسترسی (اختیاری)<input type="password" autoComplete="new-password" maxLength={8000} value={accessToken} onChange={e => setAccessToken(e.target.value)} placeholder="فقط در صورت داشتن اعتبارنامه رسمی وارد کنید"/></label>
      <div className="channels-wide channels-actions"><button disabled={busy || !organizationId}>ثبت امن کانال</button></div>
    </form></section>
    <section className="channels-panel"><div className="channels-heading"><h2>کانال‌های ثبت‌شده</h2><span>{items.length.toLocaleString("fa-IR")} مورد</span></div>
      {items.length === 0 ? <p className="channels-empty">هنوز کانالی برای این سازمان ثبت نشده است.</p> : <div className="channels-list">{items.map(item => <article className="channel-card" key={item.id}>
        <div className="channel-mark">{item.provider === "Telegram" ? "TG" : item.provider === "Facebook" ? "FB" : "IG"}</div>
        <div className="channel-info"><strong>{item.displayName}</strong><span>{providers.find(x => x.id === item.provider)?.label ?? item.provider}{item.externalAccountId ? " · " + item.externalAccountId : ""}</span><small>{statusLabel[item.status] ?? item.status}{item.hasCredential ? " · دارای اعتبارنامه رمز‌شده" : " · بدون اعتبارنامه"}</small></div>
        <button type="button" className="channels-delete" disabled={busy} onClick={() => void remove(item)}>حذف</button>
      </article>)}</div>}
    </section>
    <footer>MAJAZI CHANNELS · داده‌ها در محدوده سازمان انتخاب‌شده نگهداری می‌شوند.</footer>
  </main>;
}
