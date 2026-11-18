"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type Organization = { id: string; name: string; slug: string; createdAt: string };
type Brand = { id: string; organizationId: string; name: string; slug: string; createdAt: string };
type Member = { userId: string; email: string; role: "Owner" | "Admin" | "Member"; createdAt: string };

const modules = [
  ["مرکز هوش مصنوعی", "مدیریت مدل‌ها و عوامل هوشمند", "AI"],
  ["استودیوی محتوا", "تولید و مدیریت محتوای برندها", "CT"],
  ["تقویم انتشار", "برنامه‌ریزی و تأیید محتوا", "PL"],
  ["شبکه‌های اجتماعی", "مدیریت اتصال کانال‌ها", "CH"],
  ["مدیریت کمپین‌ها", "برنامه‌ریزی و سنجش کمپین", "CM"],
  ["مدیریت مشتریان", "پروفایل و تاریخچه ارتباط", "CR"],
  ["فرصت‌های فروش", "پیگیری سرنخ تا تبدیل", "SA"],
  ["مرکز مکالمات", "پیام‌ها و ارجاع به اپراتور", "IN"],
  ["مرکز دانش", "اطلاعات تأییدشده هر برند", "KB"],
  ["اتوماسیون", "گردش‌کارهای سازمانی", "WF"],
  ["گزارش‌ها", "شاخص‌ها و تحلیل عملکرد", "BI"],
  ["تنظیمات و دسترسی", "کاربران، نقش‌ها و امنیت", "SE"],
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
  const [memberEmail, setMemberEmail] = useState("");
  const [memberRole, setMemberRole] = useState<"Admin" | "Member">("Member");
  const [selectedId, setSelectedId] = useState("");
  const [orgName, setOrgName] = useState("");
  const [orgSlug, setOrgSlug] = useState("");
  const [brandName, setBrandName] = useState("");
  const [brandSlug, setBrandSlug] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
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
    setAuthenticated(Boolean(localStorage.getItem("majazi_access_token")));
    setAuthReady(true);
  }, []);
  useEffect(() => { if (authenticated) void loadOrganizations(); }, [authenticated, loadOrganizations]);

  useEffect(() => {
    if (!selectedId) { setBrands([]); setMembers([]); return; }
    let active = true;
    Promise.all([
      api<Brand[]>(`/api/organizations/${selectedId}/brands`),
      api<Member[]>(`/api/organizations/${selectedId}/members`),
    ]).then(([brandData, memberData]) => { if (active) { setBrands(brandData); setMembers(memberData); } })
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
  if (!authenticated) return <main className="auth-shell">
    <section className="auth-card">
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
    </section>
  </main>;

  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brand-mark">م</span><div><strong>MAJAZI</strong><small>هوشمندتر بازاریابی کن</small></div></div>
      <div className="side-label">فضای کاری</div>
      <a className="nav active" href="/">نمای کلی</a><a className="nav" href="#organizations">سازمان‌ها و برندها</a><a className="nav" href="/crm">مرکز CRM</a><a className="nav" href="/campaigns">مدیریت کمپین‌ها</a><a className="nav" href="/channels">اتصال شبکه‌ها</a><a className="nav" href="#modules">همه ماژول‌ها</a><a className="nav" href="#status">وضعیت سامانه</a>
      <div className="sidebar-bottom"><i/> هسته سامانه در حال توسعه</div>
    </aside>
    <section className="content">
      <header className="topbar"><div><span className="eyebrow">مرکز فرماندهی</span><h1>داشبورد مدیریت</h1></div><div className="profile"><span className="avatar">م</span>مدیر سامانه<button type="button" className="logout-button" onClick={logout}>خروج</button></div></header>
      <section className="welcome"><div><span className="eyebrow light">MARKETING &amp; SALES INTELLIGENCE</span><h2>بازاریابی و فروش، یکپارچه و هوشمند</h2><p>از شناخت مشتری تا تولید محتوا و مدیریت ارتباطات، همه‌چیز در یک فضای کاری.</p></div><b>م</b></section>
      <div className="section-heading" id="status"><div><h2>وضعیت هسته سامانه</h2><p>آمار سازمان‌ها و برندها مستقیماً از پایگاه داده خوانده می‌شود.</p></div><span className="pill">نسخه توسعه ۰.۲</span></div>
      <div className="stats">
        <article className="stat"><span>سازمان‌ها</span><strong>{loading ? "…" : organizations.length}</strong><small>ثبت‌شده در سامانه</small></article>
        <article className="stat"><span>برندهای سازمان منتخب</span><strong>{brands.length}</strong><small>برندهای سازمان انتخاب‌شده</small></article>
        <article className="stat"><span>کانال‌های ثبت‌شده</span><strong>—</strong><small>مشاهده در بخش اتصال شبکه‌ها</small></article>
        <article className="stat"><span>عامل‌های فعال AI</span><strong>۰</strong><small>پس از پیکربندی فعال می‌شوند</small></article>
      </div>

      <div className="section-heading" id="organizations"><div><h2>مدیریت سازمان‌ها و برندها</h2><p>ثبت و مشاهده اطلاعات پایه از طریق API و PostgreSQL</p></div><button className="refresh-button" onClick={() => void loadOrganizations()} disabled={loading}>به‌روزرسانی</button></div>
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
      <div className="module-grid">{modules.map(([name, desc, code]) => code === "CM" ? <a className="module module-link" href="/campaigns" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>قابل استفاده</small></a> : <article className="module" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>در حال توسعه</small></article>)}</div>
      <footer>MAJAZI · مرکز هوشمند بازاریابی و فروش</footer>
    </section>
  </main>;
}
