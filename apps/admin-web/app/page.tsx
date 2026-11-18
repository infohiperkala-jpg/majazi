"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type Organization = { id: string; name: string; slug: string; createdAt: string };
type Brand = { id: string; organizationId: string; name: string; slug: string; createdAt: string };

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
    headers: { "Content-Type": "application/json", ...init?.headers },
    cache: "no-store",
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok) throw new Error(payload?.error ?? "ارتباط با سرویس انجام نشد.");
  return payload as T;
}

export default function Home() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [brands, setBrands] = useState<Brand[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [orgName, setOrgName] = useState("");
  const [orgSlug, setOrgSlug] = useState("");
  const [brandName, setBrandName] = useState("");
  const [brandSlug, setBrandSlug] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

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

  useEffect(() => { void loadOrganizations(); }, [loadOrganizations]);

  useEffect(() => {
    if (!selectedId) { setBrands([]); return; }
    let active = true;
    api<Brand[]>(`/api/organizations/${selectedId}/brands`)
      .then((data) => { if (active) setBrands(data); })
      .catch((e: unknown) => { if (active) setError(e instanceof Error ? e.message : "دریافت برندها ناموفق بود."); });
    return () => { active = false; };
  }, [selectedId]);

  async function createOrganization(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setSaving(true); setError(""); setNotice("");
    try {
      const created = await api<Organization>("/api/organizations", { method: "POST", body: JSON.stringify({ name: orgName, slug: orgSlug }) });
      setOrganizations((items) => [...items, created].sort((a, b) => a.name.localeCompare(b.name)));
      setSelectedId(created.id); setOrgName(""); setOrgSlug(""); setNotice("سازمان با موفقیت ثبت شد.");
    } catch (e) { setError(e instanceof Error ? e.message : "ثبت سازمان ناموفق بود."); }
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

  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brand-mark">م</span><div><strong>MAJAZI</strong><small>هوشمندتر بازاریابی کن</small></div></div>
      <div className="side-label">فضای کاری</div>
      <a className="nav active" href="/">نمای کلی</a><a className="nav" href="#organizations">سازمان‌ها و برندها</a><a className="nav" href="#modules">همه ماژول‌ها</a><a className="nav" href="#status">وضعیت سامانه</a>
      <div className="sidebar-bottom"><i/> هسته سامانه در حال توسعه</div>
    </aside>
    <section className="content">
      <header className="topbar"><div><span className="eyebrow">مرکز فرماندهی</span><h1>داشبورد مدیریت</h1></div><div className="profile"><span className="avatar">م</span>مدیر سامانه</div></header>
      <section className="welcome"><div><span className="eyebrow light">MARKETING &amp; SALES INTELLIGENCE</span><h2>بازاریابی و فروش، یکپارچه و هوشمند</h2><p>از شناخت مشتری تا تولید محتوا و مدیریت ارتباطات، همه‌چیز در یک فضای کاری.</p></div><b>م</b></section>
      <div className="section-heading" id="status"><div><h2>وضعیت هسته سامانه</h2><p>آمار سازمان‌ها و برندها مستقیماً از پایگاه داده خوانده می‌شود.</p></div><span className="pill">نسخه توسعه ۰.۲</span></div>
      <div className="stats">
        <article className="stat"><span>سازمان‌ها</span><strong>{loading ? "…" : organizations.length}</strong><small>ثبت‌شده در سامانه</small></article>
        <article className="stat"><span>برندهای سازمان منتخب</span><strong>{brands.length}</strong><small>برندهای سازمان انتخاب‌شده</small></article>
        <article className="stat"><span>کانال‌های متصل</span><strong>۰</strong><small>هنوز اتصالی تأیید نشده</small></article>
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
            <div className="org-list">{organizations.map((org) => <button type="button" key={org.id} className={selectedId === org.id ? "org-item selected" : "org-item"} onClick={() => setSelectedId(org.id)}><span><strong>{org.name}</strong><small dir="ltr">{org.slug}</small></span><b>{selectedId === org.id ? "انتخاب‌شده" : "مشاهده برندها"}</b></button>)}</div>}
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
            {brands.length === 0 ? <p className="muted">برای این سازمان برندی ثبت نشده است.</p> : <div className="org-list">{brands.map((brand) => <div className="org-item" key={brand.id}><span><strong>{brand.name}</strong><small dir="ltr">{brand.slug}</small></span><b>فعال</b></div>)}</div>}
          </> : <p className="muted">ابتدا یک سازمان ایجاد یا انتخاب کنید.</p>}
        </section>
      </div>

      <div className="section-heading" id="modules"><div><h2>ماژول‌های سامانه</h2><p>ساختار فضای کاری MAJAZI</p></div></div>
      <div className="module-grid">{modules.map(([name, desc, code]) => <article className="module" key={code}><span className="module-icon">{code}</span><div><h3>{name}</h3><p>{desc}</p></div><span className="arrow">←</span><small>در حال توسعه</small></article>)}</div>
      <footer>MAJAZI · مرکز هوشمند بازاریابی و فروش</footer>
    </section>
  </main>;
}
