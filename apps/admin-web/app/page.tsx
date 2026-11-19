"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type Organization = { id: string; name: string; slug: string; createdAt: string };
type Brand = { id: string; organizationId: string; name: string; slug: string; createdAt: string };
type Member = { userId: string; email: string; role: "Owner" | "Admin" | "Member"; createdAt: string };
type DashboardSummary = { contacts: number; leads: number; openLeads: number; campaigns: number; activeCampaigns: number; connectedChannels: number; totalChannels: number; members: number; budget: number; spend: number; attributedRevenue: number; impressions: number; clicks: number; conversions: number; };

const modules = [
  ["داشبورد مدیریت کل", "نمای کلی شاخص‌ها و فعالیت سامانه", "DB"],
  ["مدیریت سازمان‌ها و برندها", "فضاهای کاری، اعضا و هویت برند", "OR"],
  ["مرکز هوش مصنوعی", "مدل‌ها، کلیدها و عوامل هوشمند", "AI"],
  ["استودیوی تولید محتوا", "تولید متن، تصویر و محتوای برند", "CT"],
  ["تقویم محتوایی", "زمان‌بندی، بازبینی و تأیید انتشار", "PL"],
  ["مدیریت شبکه‌های اجتماعی", "اتصال و مدیریت کانال‌ها", "CH"],
  ["مدیریت کمپین‌های تبلیغاتی", "طراحی، اجرا و سنجش کمپین", "CM"],
  ["مرکز پیام‌ها و مکالمات", "صندوق ورودی یکپارچه و ارجاع", "IN"],
  ["مرکز تماس هوشمند", "تماس‌ها، صف و دستیار مکالمه", "CC"],
  ["مدیریت اپراتورهای هوشمند", "تعریف دستیارها و سطح دسترسی", "OP"],
  ["مدیریت مشتریان CRM", "پروفایل، تعاملات و بخش‌بندی", "CR"],
  ["مدیریت سرنخ‌های فروش", "قیف فروش و پیگیری فرصت‌ها", "LD"],
  ["مدیریت فرصت‌های فروش", "پیشنهادها، مذاکرات و تبدیل", "SA"],
  ["مدیریت سفارش‌ها و پرداخت‌ها", "سفارش، فاکتور و تراکنش", "OD"],
  ["اتوماسیون و گردش‌کار", "قواعد، محرک‌ها و عملیات خودکار", "WF"],
  ["مرکز دانش و اطلاعات کسب‌وکار", "دانش‌نامه و منابع تأییدشده", "KB"],
  ["مدیریت کاربران و تیم", "اعضا، نقش‌ها و مجوزها", "TM"],
  ["گزارش‌ها و هوش تجاری", "گزارش عملکرد و تحلیل شاخص‌ها", "BI"],
  ["مدیریت هزینه‌ها و عملکرد", "بودجه، هزینه و بازدهی", "FN"],
  ["مدیریت وضعیت‌های سازمان", "تعریف و ویرایش وضعیت‌های CRM، کمپین و مکالمات", "ST"],
  ["کتابخانه متن‌های آماده", "قالب‌های قابل ویرایش و درج در فرم‌ها", "TX"],
  ["گزارش رویدادها و حسابرسی", "ردیابی عملیات مدیریتی و تغییرات سازمان", "AU"],
  ["امنیت و تنظیمات", "پیکربندی فضای کاری و برندها", "SE"],
];

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/backend-api${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...(typeof window !== "undefined" && localStorage.getItem("majazi_access_token") ? { Authorization: `Bearer ${localStorage.getItem("majazi_access_token")}` } : {}), ...init?.headers },
    cache: "no-store",
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok) throw new Error(payload?.error ?? "ارتباط با سرویس انجام نشد.");
  return payload as T;
}

export default function Home() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [brands, setBrands] = useState<Brand[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [dashboard, setDashboard] = useState<DashboardSummary | null>(null);
  const [memberEmail, setMemberEmail] = useState("");
  const [memberRole, setMemberRole] = useState<"Admin" | "Member">("Member");
  const [selectedId, setSelectedId] = useState("");
  const [orgName, setOrgName] = useState("");
  const [orgSlug, setOrgSlug] = useState("");
  const [brandName, setBrandName] = useState("");
  const [brandSlug, setBrandSlug] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [seedingDemo, setSeedingDemo] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [authReady, setAuthReady] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);
  const [authMode, setAuthMode] = useState<"login" | "register">("login");
  const [authEmail, setAuthEmail] = useState("");
  const [authPassword, setAuthPassword] = useState("");
  const [authOrgName, setAuthOrgName] = useState("");
  const [captchaId, setCaptchaId] = useState("");
  const [captchaImage, setCaptchaImage] = useState("");
  const [captchaAnswer, setCaptchaAnswer] = useState("");
  const [captchaBusy, setCaptchaBusy] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [authBusy, setAuthBusy] = useState(false);
  const [authError, setAuthError] = useState("");

  async function refreshCaptcha() {
    setCaptchaBusy(true);
    try {
      const challenge = await api<{ captchaId: string; imageDataUrl: string }>("/api/auth/captcha");
      setCaptchaId(challenge.captchaId);
      setCaptchaImage(challenge.imageDataUrl);
      setCaptchaAnswer("");
    } catch (e) {
      setAuthError(e instanceof Error ? e.message : "دریافت تصویر امنیتی ناموفق بود.");
    } finally { setCaptchaBusy(false); }
  }

  const loadOrganizations = useCallback(async () => {
    setLoading(true);
    try {
      const data = await api<Organization[]>("/api/organizations");
      setOrganizations(data);
      setSelectedId((current) => current && data.some((x) => x.id === current) ? current : data[0]?.id ?? "");
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "دریافت سازمان‌ها ناموفق بود.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refreshCaptcha();
  }, [authMode]);

  useEffect(() => {
    let active = true;
    const token = localStorage.getItem("majazi_access_token");
    if (!token) {
      setAuthReady(true);
      return () => { active = false; };
    }

    // Validate the saved token with the API before opening protected pages.
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 7000);

    fetch("/backend-api/api/auth/me", {
      headers: { Authorization: "Bearer " + token },
      cache: "no-store",
      signal: controller.signal,
    }).then((response) => {
      if (!active) return;
      if (response.ok) {
        setAuthenticated(true);
        setAuthError("");
      } else if (response.status === 401 || response.status === 403) {
        localStorage.removeItem("majazi_access_token");
        setAuthenticated(false);
        setAuthError("نشست شما معتبر نیست؛ دوباره وارد شوید.");
      } else {
        // Fail closed: never expose protected pages when session validation failed.
        setAuthenticated(false);
        setAuthError("اعتبارسنجی نشست انجام نشد. اتصال سرویس را بررسی و دوباره وارد شوید.");
      }
    }).catch(() => {
      if (!active) return;
      setAuthenticated(false);
      setAuthError("ارتباط با سرور برای بررسی نشست برقرار نشد. پس از بررسی اتصال دوباره تلاش کنید.");
    }).finally(() => {
      window.clearTimeout(timeout);
      if (active) setAuthReady(true);
    });

    return () => { active = false; controller.abort(); window.clearTimeout(timeout); };
  }, []);
  useEffect(() => { if (authenticated) void loadOrganizations(); }, [authenticated, loadOrganizations]);

  useEffect(() => {
    if (!selectedId) { setBrands([]); setMembers([]); setDashboard(null); return; }
    let active = true;
    Promise.all([
      api<Brand[]>(`/api/organizations/${selectedId}/brands`),
      api<Member[]>(`/api/organizations/${selectedId}/members`),
      api<DashboardSummary>(`/api/organizations/${selectedId}/dashboard/summary`),
    ]).then(([brandData, memberData, dashboardData]) => {
      if (active) { setBrands(brandData); setMembers(memberData); setDashboard(dashboardData); }
    })
      .catch((e: unknown) => { if (active) setError(e instanceof Error ? e.message : "دریافت اطلاعات سازمان ناموفق بود."); });
    return () => { active = false; };
  }, [selectedId]);

  async function submitAuth(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setAuthBusy(true); setAuthError("");
    try {
      const payload = authMode === "login"
        ? { email: authEmail, password: authPassword, captchaId, captchaAnswer }
        : { email: authEmail, password: authPassword, organizationName: authOrgName, captchaId, captchaAnswer };
      const result = await api<{ accessToken: string }>(authMode === "login" ? "/api/auth/login" : "/api/auth/register", {
        method: "POST", body: JSON.stringify(payload),
      });
      localStorage.setItem("majazi_access_token", result.accessToken);
      setAuthenticated(true);
    } catch (e) {
      setAuthError(e instanceof Error ? e.message : "ورود یا ثبت‌نام انجام نشد.");
      await refreshCaptcha();
    } finally { setAuthBusy(false); }
  }

  function logout() {
    localStorage.removeItem("majazi_access_token");
    setAuthenticated(false); setOrganizations([]); setBrands([]); setSelectedId("");
  }

  async function createOrganization(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setSaving(true); setError(""); setNotice("");
    try {
      const created = await api<Organization>("/api/organizations", { method: "POST", body: JSON.stringify({ name: orgName, slug: orgSlug }) });
      setOrganizations((items) => [...items, created].sort((a, b) => a.name.localeCompare(b.name)));
      setSelectedId(created.id); setOrgName(""); setOrgSlug(""); setNotice("سازمان با موفقیت ثبت شد.");
    } catch (e) { setError(e instanceof Error ? e.message : "ثبت سازمان ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function createDemoWorkspace() {
    setSeedingDemo(true); setError(""); setNotice("");
    try {
      const result = await api<{ organizationId: string; organization: string; counts: { contacts: number; leads: number; activities: number; campaigns: number; contentDrafts: number; inboxConversations: number } }>("/api/organizations/demo/digibail", { method: "POST" });
      await loadOrganizations();
      setSelectedId(result.organizationId);
      setNotice(`فضای نمونه دیجی‌بایل آماده شد: ${result.counts.contacts} مشتری، ${result.counts.leads} سرنخ، ${result.counts.campaigns} کمپین، ${result.counts.contentDrafts} محتوا و ${result.counts.inboxConversations} گفت‌وگو. داده‌ها آزمایشی‌اند و اتصال جعلی ایجاد نشده است.`);
    } catch (e) { setError(e instanceof Error ? e.message : "ساخت فضای نمونه انجام نشد."); }
    finally { setSeedingDemo(false); }
  }

  async function addMember(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!selectedId) return;
    setSaving(true); setError(""); setNotice("");
    try {
      const created = await api<Member>(`/api/organizations/${selectedId}/members`, {
        method: "POST", body: JSON.stringify({ email: memberEmail, role: memberRole }),
      });
      setMembers(items => [...items, created].sort((a, b) => a.email.localeCompare(b.email)));
      setMemberEmail(""); setNotice("عضو جدید به سازمان اضافه شد.");
    } catch (e) { setError(e instanceof Error ? e.message : "افزودن عضو ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function removeMember(member: Member) {
    if (!selectedId || !window.confirm(`عضویت ${member.email} از این سازمان لغو شود؟`)) return;
    setSaving(true); setError(""); setNotice("");
    try {
      await api<void>(`/api/organizations/${selectedId}/members/${member.userId}`, { method: "DELETE" });
      setMembers(items => items.filter(x => x.userId !== member.userId));
      setNotice("عضویت کاربر لغو شد.");
    } catch (e) { setError(e instanceof Error ? e.message : "حذف عضو ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function deleteOrganization(org: Organization) {
    if (!window.confirm(`سازمان «${org.name}» و تمام برندهای آن حذف شود؟`)) return;
    setSaving(true); setError(""); setNotice("");
    try {
      await api<void>(`/api/organizations/${org.id}`, { method: "DELETE" });
      setOrganizations((items) => items.filter((x) => x.id !== org.id));
      if (selectedId === org.id) { setSelectedId(""); setBrands([]); }
      setNotice("سازمان و برندهای وابسته حذف شدند.");
    } catch (e) { setError(e instanceof Error ? e.message : "حذف سازمان ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function deleteBrand(brand: Brand) {
    if (!window.confirm(`برند «${brand.name}» حذف شود؟`)) return;
    setSaving(true); setError(""); setNotice("");
    try {
      await api<void>(`/api/brands/${brand.id}`, { method: "DELETE" });
      setBrands((items) => items.filter((x) => x.id !== brand.id));
      setNotice("برند حذف شد.");
    } catch (e) { setError(e instanceof Error ? e.message : "حذف برند ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function createBrand(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!selectedId) return;
    setSaving(true); setError(""); setNotice("");
    try {
      const created = await api<Brand>(`/api/organizations/${selectedId}/brands`, { method: "POST", body: JSON.stringify({ name: brandName, slug: brandSlug }) });
      setBrands((items) => [...items, created].sort((a, b) => a.name.localeCompare(b.name)));
      setBrandName(""); setBrandSlug(""); setNotice("برند با موفقیت ثبت شد.");
    } catch (e) { setError(e instanceof Error ? e.message : "ثبت برند ناموفق بود."); }
    finally { setSaving(false); }
  }

  if (!authReady) return <main className="auth-shell"><section className="auth-card">در حال آماده‌سازی فضای امن…</section></main>;
  if (!authenticated) return <main className="auth-shell auth-premium">
    <section className="auth-showcase" aria-label="معرفی MAJAZI">
      <div className="showcase-orbit orbit-one" /><div className="showcase-orbit orbit-two" />
      <div className="showcase-top"><span className="showcase-symbol">M</span><span>MAJAZI <small>INTELLIGENT BUSINESS SUITE</small></span></div>
      <div className="showcase-copy"><span className="showcase-kicker">THE FUTURE OF GROWTH</span><h2>تمام کسب‌وکار شما،<br/><em>هوشمند و یکپارچه.</em></h2><p>بازاریابی، فروش، ارتباط با مشتری و هوش مصنوعی؛ در یک مرکز فرماندهی قدرتمند.</p></div>
      <div className="showcase-visual"><div className="visual-glow"/><div className="visual-core"><span>م</span><b>MAJAZI AI</b><small>BUSINESS INTELLIGENCE</small></div><div className="float-card float-card-a"><i/> <span>هوش مصنوعی فعال</span><b>ONLINE</b></div><div className="float-card float-card-b"><span className="mini-bars"><i/><i/><i/><i/><i/></span><span>رشد هوشمند کسب‌وکار</span></div></div>
      <div className="showcase-foot"><span>SECURE WORKSPACE</span><span>قدرت گرفته از هوش مصنوعی</span></div>
    </section>
    <section className="auth-panel"><div className="auth-card">
      <div className="brand auth-brand"><span className="brand-mark">م</span><div><strong>MAJAZI</strong><small>فضای کاری هوشمند</small></div></div>
      <h1>{authMode === "login" ? "ورود به حساب کاربری" : "ساخت فضای کاری جدید"}</h1>
      <p className="auth-description">{authMode === "login" ? "برای دسترسی به سازمان‌ها و ابزارهای مدیریتی وارد شوید." : "حساب مدیر و سازمان اولیه شما به‌صورت امن ایجاد می‌شود."}</p>
      {authError && <div className="feedback error" role="alert">{authError}</div>}
      <form className="data-form" onSubmit={submitAuth}>
        <label>ایمیل<input type="email" value={authEmail} onChange={e => setAuthEmail(e.target.value)} autoComplete="email" required dir="ltr" placeholder="name@example.com" /></label>
        <label>گذرواژه<div className="password-field"><input type={showPassword ? "text" : "password"} value={authPassword} onChange={e => setAuthPassword(e.target.value)} autoComplete={authMode === "login" ? "current-password" : "new-password"} minLength={authMode === "register" ? 12 : 1} required dir="ltr" placeholder={authMode === "register" ? "حداقل ۱۲ نویسه، حروف بزرگ و کوچک و عدد" : "گذرواژه"} /><button type="button" className="password-toggle" onClick={() => setShowPassword(v => !v)} aria-label={showPassword ? "مخفی کردن گذرواژه" : "نمایش گذرواژه"}>{showPassword ? "مخفی" : "نمایش"}</button></div></label>
        {authMode === "register" && <>
          <label>نام سازمان<input value={authOrgName} onChange={e => setAuthOrgName(e.target.value)} maxLength={180} required placeholder="نام سازمان شما" /></label>
          <div className="generated-id-note">شناسه یکتای سازمان به‌صورت خودکار و غیرتکراری توسط سامانه ساخته می‌شود.</div>
        </>}
        <div className="captcha-box"><div className="captcha-heading"><span>کد امنیتی تصویری</span><button type="button" onClick={() => void refreshCaptcha()} disabled={captchaBusy}>تغییر تصویر</button></div><div className="captcha-image">{captchaImage ? <img src={captchaImage} alt="کد امنیتی تصویری" /> : <span>در حال دریافت تصویر…</span>}</div><label>اعداد داخل تصویر را وارد کنید<input value={captchaAnswer} onChange={e => setCaptchaAnswer(e.target.value.replace(/\D/g, "").slice(0, 5))} inputMode="numeric" autoComplete="off" required dir="ltr" maxLength={5} placeholder="۵ رقم" /></label></div>
        <button type="submit" disabled={authBusy}>{authBusy ? "لطفاً صبر کنید…" : authMode === "login" ? "ورود امن" : "ایجاد حساب و سازمان"}</button>
      </form>
      <button type="button" className="auth-switch" onClick={() => { setAuthMode(authMode === "login" ? "register" : "login"); setAuthError(""); setShowPassword(false); }}>
        {authMode === "login" ? "حساب ندارید؟ ثبت‌نام کنید" : "قبلاً حساب ساخته‌اید؟ وارد شوید"}
      </button>
    </div></section>
  </main>;

  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brand-mark">م</span><div><strong>MAJAZI</strong><small>مرکز هوشمند بازاریابی و فروش</small></div></div>
      <div className="side-label">مدیریت فضای کاری</div>
      <a className="nav active" href="#status"><span>۱.</span> داشبورد مدیریت کل</a>
      <a className="nav" href="#organizations"><span>۲.</span> مدیریت سازمان‌ها و برندها</a>
      <a className="nav" href="#modules"><span>۳.</span> مرکز هوش مصنوعی</a>
      <a className="nav" href="#modules"><span>۴.</span> استودیوی تولید محتوا</a>
      <a className="nav" href="#modules"><span>۵.</span> تقویم محتوایی</a>
      <a className="nav" href="#channels"><span>۶.</span> مدیریت شبکه‌های اجتماعی</a>
      <a className="nav" href="/growth"><span>۷.</span> مرکز رشد چندسکویی و افزایش فروش</a>
      <a className="nav" href="#modules"><span>۷.</span> مدیریت کمپین‌های تبلیغاتی</a>
      <a className="nav" href="#modules"><span>۸.</span> مرکز پیام‌ها و مکالمات</a>
      <a className="nav" href="#modules"><span>۹.</span> مرکز تماس هوشمند</a>
      <a className="nav" href="#modules"><span>۱۰.</span> مدیریت اپراتورهای هوشمند</a>
      <a className="nav" href="/crm"><span>۱۱.</span> مدیریت مشتریان</a>
      <a className="nav" href="#modules"><span>۱۲.</span> مدیریت سرنخ‌های فروش</a>
      <a className="nav" href="#modules"><span>۱۳.</span> مدیریت فرصت‌های فروش</a>
      <a className="nav" href="#modules"><span>۱۴.</span> مدیریت سفارش‌ها و پرداخت‌ها</a>
      <a className="nav" href="#modules"><span>۱۵.</span> اتوماسیون و گردش‌کار</a>
      <a className="nav" href="#modules"><span>۱۶.</span> مرکز دانش و اطلاعات کسب‌وکار</a>
      <a className="nav" href="#modules"><span>۱۷.</span> مدیریت کاربران و تیم</a>
      <a className="nav" href="#modules"><span>۱۸.</span> گزارش‌ها و هوش تجاری</a>
      <a className="nav" href="#modules"><span>۱۹.</span> مدیریت هزینه‌ها و عملکرد</a>
      <a className="nav" href="#modules"><span>۲۰.</span> امنیت، حسابرسی و تنظیمات</a>
      <a className="nav" href="/statuses"><span>۲۱.</span> مدیریت وضعیت‌های سازمان</a>
      <a className="nav" href="/templates"><span>۲۲.</span> کتابخانه متن‌های آماده</a>
      <div className="sidebar-bottom"><i/> اتصال زنده به هسته سامانه</div>
    </aside>
    <section className="content">
      <header className="topbar"><div><span className="eyebrow">مرکز فرماندهی</span><h1>داشبورد مدیریت</h1></div><div className="topbar-work-actions"><a href="/content">مشاهده و ویرایش محتوا</a><a href="/content?mode=ai">✦ ایجاد با هوش مصنوعی</a><a href="/content?mode=manual">＋ ایجاد دستی</a><a href="/channels">اتصال پیام‌رسان‌ها</a></div><div className="profile"><span className="avatar">م</span>مدیر سامانه<button type="button" className="logout-button" onClick={logout}>خروج</button></div></header>
      <div className="section-heading" id="status"><div><h2>داشبورد مدیریت کل</h2><p>نمای کلی وضعیت سازمان، ارتباطات و عملکرد بازاریابی</p></div><span className="pill">نسخه توسعه</span></div>
      <div className="stats">
        <article className="stat"><span>سازمان‌های فعال</span><strong>{loading ? "…" : organizations.length}</strong><small>داده مستقیم از پایگاه داده</small></article>
        <article className="stat"><span>کانال‌های متصل</span><strong>{dashboard?.connectedChannels ?? "—"}</strong><small>{dashboard ? `از ${dashboard.totalChannels} کانال ثبت‌شده` : "سازمان را انتخاب کنید"}</small></article>
        <article className="stat"><span>مشتریان CRM</span><strong>{dashboard?.contacts ?? "—"}</strong><small>مخاطبان ثبت‌شده در سازمان</small></article>
        <article className="stat"><span>سرنخ‌های فروش</span><strong>{dashboard?.leads ?? "—"}</strong><small>{dashboard ? `${dashboard.openLeads} سرنخ باز` : "داده سازمان"}</small></article>
        <article className="stat"><span>کمپین‌های بازاریابی</span><strong>{dashboard?.campaigns ?? "—"}</strong><small>{dashboard ? `${dashboard.activeCampaigns} کمپین فعال` : "داده سازمان"}</small></article>
        <article className="stat"><span>بودجه کمپین‌ها</span><strong>{dashboard ? dashboard.budget.toLocaleString("en-US") : "—"}</strong><small>مجموع بودجه ثبت‌شده</small></article>
        <article className="stat"><span>هزینه انجام‌شده</span><strong>{dashboard ? dashboard.spend.toLocaleString("en-US") : "—"}</strong><small>مجموع هزینه واقعی کمپین‌ها</small></article>
        <article className="stat"><span>اعضای سازمان منتخب</span><strong>{dashboard?.members ?? members.length}</strong><small>اعضای دارای دسترسی</small></article>
        <article className="stat"><span>درآمد منتسب به کمپین‌ها</span><strong>{dashboard ? dashboard.attributedRevenue.toLocaleString("en-US") : "—"}</strong><small>مجموع درآمد ثبت‌شده</small></article>
        <article className="stat"><span>نمایش تبلیغات</span><strong>{dashboard ? dashboard.impressions.toLocaleString("en-US") : "—"}</strong><small>مجموع نمایش کمپین‌ها</small></article>
        <article className="stat"><span>کلیک‌های کمپین</span><strong>{dashboard ? dashboard.clicks.toLocaleString("en-US") : "—"}</strong><small>مجموع کلیک ثبت‌شده</small></article>
        <article className="stat"><span>تبدیل‌های کمپین</span><strong>{dashboard ? dashboard.conversions.toLocaleString("en-US") : "—"}</strong><small>مجموع تبدیل ثبت‌شده</small></article>
      </div>
      <div className="section-heading" id="channels"><div><h2>وضعیت کانال‌های ارتباطی</h2><p>وضعیت فعلی اتصال‌ها؛ اتصال واقعی پس از ثبت اعتبارنامه هر سرویس فعال می‌شود.</p></div></div>
      <section className="panel channel-panel"><div className="channel-table-wrap"><table className="channel-table"><thead><tr><th>کانال</th><th>نوع</th><th>وضعیت اتصال</th><th>آخرین تبادل داده</th></tr></thead><tbody>
        {["Instagram","WhatsApp","Telegram","YouTube","TikTok","Facebook","ایتا","بله","روبیکا","سروش","گپ"].map((channel,i)=><tr key={channel}><td dir={i<6?"ltr":"rtl"}>{channel}</td><td>{i<6?"خارجی":"ایرانی"}</td><td><span className="connection-status">متصل نشده</span></td><td>—</td></tr>)}
      </tbody></table></div></section>

      <div className="section-heading" id="organizations"><div><h2>مدیریت سازمان‌ها و برندها</h2><p>ثبت و مشاهده اطلاعات پایه از طریق API و PostgreSQL</p></div><div className="topbar-work-actions"><button className="refresh-button" onClick={() => void createDemoWorkspace()} disabled={seedingDemo || saving}>{seedingDemo ? "در حال ساخت داده‌ها…" : "ساخت فضای نمونه دیجی‌بایل"}</button><button className="refresh-button" onClick={() => void loadOrganizations()} disabled={loading}>به‌روزرسانی</button></div></div>
      {error && <div className="feedback error" role="alert">{error}</div>}
      {notice && <div className="feedback success" role="status">{notice}</div>}
      <div className="management-grid">
        <section className="panel">
          <h3>ایجاد سازمان جدید</h3>
          <form className="data-form" onSubmit={createOrganization}>
            <label>نام سازمان<input value={orgName} onChange={(e) => setOrgName(e.target.value)} maxLength={180} required placeholder="مثلاً گروه بازرگانی..." /></label>
            <label>شناسه یکتا<input value={orgSlug} onChange={(e) => setOrgSlug(e.target.value)} maxLength={100} required dir="ltr" placeholder="company-name" /></label>
            <button type="submit" disabled={saving}>{saving ? "در حال ثبت…" : "ثبت سازمان"}</button>
          </form>
          <h3 className="subheading">فهرست سازمان‌ها</h3>
          {loading ? <p className="muted">در حال دریافت اطلاعات…</p> : organizations.length === 0 ? <p className="muted">هنوز سازمانی ثبت نشده است.</p> :
            <div className="org-list">{organizations.map((org) => <div className="org-row" key={org.id}><button type="button" className={selectedId === org.id ? "org-item selected" : "org-item"} onClick={() => setSelectedId(org.id)}><span><strong>{org.name}</strong><small dir="ltr">{org.slug}</small></span><b>{selectedId === org.id ? "انتخاب‌شده" : "مشاهده برندها"}</b></button><button type="button" className="delete-button" disabled={saving} onClick={() => void deleteOrganization(org)} aria-label={`حذف سازمان ${org.name}`}>حذف</button></div>)}</div>}
        </section>
        <section className="panel">
          <h3>مدیریت برند سازمان</h3>
          {selectedId ? <>
            <p className="muted">سازمان انتخاب‌شده: <strong>{organizations.find((x) => x.id === selectedId)?.name}</strong></p>
            <form className="data-form" onSubmit={createBrand}>
              <label>نام برند<input value={brandName} onChange={(e) => setBrandName(e.target.value)} maxLength={180} required placeholder="نام برند" /></label>
              <label>شناسه برند<input value={brandSlug} onChange={(e) => setBrandSlug(e.target.value)} maxLength={100} required dir="ltr" placeholder="brand-name" /></label>
              <button type="submit" disabled={saving}>{saving ? "در حال ثبت…" : "ثبت برند"}</button>
            </form>
            <h3 className="subheading">برندهای ثبت‌شده</h3>
            {brands.length === 0 ? <p className="muted">برای این سازمان برندی ثبت نشده است.</p> : <div className="org-list">{brands.map((brand) => <div className="org-row" key={brand.id}><div className="org-item"><span><strong>{brand.name}</strong><small dir="ltr">{brand.slug}</small></span><b>فعال</b></div><button type="button" className="delete-button" disabled={saving} onClick={() => void deleteBrand(brand)} aria-label={`حذف برند ${brand.name}`}>حذف</button></div>)}</div>}
            <h3 className="subheading">اعضای سازمان</h3>
            <form className="member-form" onSubmit={addMember}>
              <label>ایمیل کاربر<input type="email" value={memberEmail} onChange={e => setMemberEmail(e.target.value)} required dir="ltr" placeholder="user@example.com" /></label>
              <label>نقش<select value={memberRole} onChange={e => setMemberRole(e.target.value as "Admin" | "Member")}><option value="Member">عضو</option><option value="Admin">مدیر</option></select></label>
              <button type="submit" disabled={saving}>افزودن عضو</button>
            </form>
            <div className="org-list member-list">{members.map(member => <div className="org-row" key={member.userId}><div className="org-item"><span><strong dir="ltr">{member.email}</strong><small>{member.role === "Owner" ? "مالک" : member.role === "Admin" ? "مدیر" : "عضو"}</small></span><b>{member.role}</b></div>{member.role !== "Owner" && <button type="button" className="delete-button" disabled={saving} onClick={() => void removeMember(member)}>لغو عضویت</button>}</div>)}</div>
          </> : <p className="muted">ابتدا یک سازمان ایجاد یا انتخاب کنید.</p>}
        </section>
      </div>

      <div className="section-heading" id="modules"><div><h2>ماژول‌های سامانه</h2><p>ساختار فضای کاری MAJAZI</p></div></div>
      <div className="module-grid">{modules.map(([name, desc, code]) => code === "CT" ? <a className="module module-link" href="/content" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>فعال · استودیوی محتوا</small></a> : code === "CM" ? <a className="module module-link" href="/campaigns" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>قابل استفاده</small></a> : code === "CH" ? <a className="module module-link" href="/channels" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>فعال · کانال‌ها</small></a> : code === "CR" ? <a className="module module-link" href="/crm" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>فعال · CRM</small></a> : code === "IN" ? <a className="module module-link" href="/inbox" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>فعال · صندوق پیام‌ها</small></a> : code === "ST" ? <a className="module module-link" href="/statuses" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>مدیریت وضعیت‌های قابل تنظیم</small></a> : code === "TX" ? <a className="module module-link" href="/templates" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>قالب‌های متنی سازمان</small></a> : code === "AI" ? <a className="module module-link" href="/ai" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>وضعیت اتصال و تولید متن</small></a> : code === "TM" ? <a className="module module-link" href="/team" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>مدیریت اعضا و نقش‌ها</small></a> : code === "BI" ? <a className="module module-link" href="/reports" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>گزارش زنده از داده‌های سازمان</small></a> : code === "PL" ? <a className="module module-link" href="/content" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>تقویم، زمان‌بندی و تأیید محتوا</small></a> : code === "OP" ? <a className="module module-link" href="/growth" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>عامل رشد و برنامه‌ریزی هوشمند</small></a> : code === "LD" || code === "SA" ? <a className="module module-link" href="/crm" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>قیف فروش و فرصت‌ها در CRM</small></a> : code === "FN" ? <a className="module module-link" href="/reports" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>هزینه، بودجه و بازدهی کمپین</small></a> : code === "KB" ? <a className="module module-link" href="/knowledge" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>دانش‌نامه، جست‌وجو و نسخه‌بندی</small></a> : code === "WF" ? <a className="module module-link" href="/workflows" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>قواعد رویدادمحور و سابقه اجرا</small></a> : code === "AU" ? <a className="module module-link" href="/audit" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>تاریخچه عملیات حساس و تغییرات سازمان</small></a> : code === "SE" ? <a className="module module-link" href="/settings" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>تنظیمات سازمان و برندها</small></a> : <article className="module" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>در حال توسعه</small></article>)}</div>
      <footer>MAJAZI · مرکز هوشمند بازاریابی و فروش</footer>
    </section>
  </main>;
}
