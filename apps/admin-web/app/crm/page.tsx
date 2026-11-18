"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import "./crm.css";

type Organization = { id: string; name: string; role: string };
type Contact = { id: string; fullName: string; email?: string | null; phone?: string | null; company?: string | null; notes?: string | null; brandId?: string | null; createdAt: string };
type Lead = { id: string; title: string; stage: string; estimatedValue?: number | null; source?: string | null; notes?: string | null; contactId?: string | null; contactName?: string | null; createdAt: string; updatedAt: string };
type Activity = { id: string; activityType: string; summary: string; occurredAt: string; contactId?: string | null; leadId?: string | null };
const stages = [{ id: "New", label: "جدید" }, { id: "Qualified", label: "ارزیابی‌شده" }, { id: "Proposal", label: "پیشنهاد" }, { id: "Negotiation", label: "مذاکره" }, { id: "Won", label: "موفق" }, { id: "Lost", label: "از دست‌رفته" }];
const activityTypes = [{ id: "Note", label: "یادداشت" }, { id: "Call", label: "تماس" }, { id: "Email", label: "ایمیل" }, { id: "Meeting", label: "جلسه" }, { id: "Task", label: "وظیفه" }];

async function request<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/backend-api${path}`, { ...init, headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`, ...init?.headers }, cache: "no-store" });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? "عملیات انجام نشد.");
  return data as T;
}

export default function CrmPage() {
  const [token, setToken] = useState("");
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [leads, setLeads] = useState<Lead[]>([]);
  const [activities, setActivities] = useState<Activity[]>([]);
  const [editingContactId, setEditingContactId] = useState("");
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [phone, setPhone] = useState("");
  const [company, setCompany] = useState("");
  const [contactNotes, setContactNotes] = useState("");
  const [editingLeadId, setEditingLeadId] = useState("");
  const [leadTitle, setLeadTitle] = useState("");
  const [leadContactId, setLeadContactId] = useState("");
  const [leadValue, setLeadValue] = useState("");
  const [leadSource, setLeadSource] = useState("");
  const [leadNotes, setLeadNotes] = useState("");
  const [activityType, setActivityType] = useState("Note");
  const [activitySummary, setActivitySummary] = useState("");
  const [activityContactId, setActivityContactId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  async function reload(orgId = organizationId, accessToken = token) {
    if (!orgId || !accessToken) return;
    const [c, l, a] = await Promise.all([
      request<Contact[]>(`/api/organizations/${orgId}/crm/contacts`, accessToken),
      request<Lead[]>(`/api/organizations/${orgId}/crm/leads`, accessToken),
      request<Activity[]>(`/api/organizations/${orgId}/crm/activities`, accessToken),
    ]);
    setContacts(c); setLeads(l); setActivities(a);
  }

  useEffect(() => {
    const saved = localStorage.getItem("majazi_access_token") ?? "";
    setToken(saved);
    if (!saved) return;
    request<Organization[]>("/api/organizations", saved)
      .then(items => { setOrganizations(items); setOrganizationId(items[0]?.id ?? ""); })
      .catch(e => setError(e instanceof Error ? e.message : "دریافت سازمان‌ها ناموفق بود."));
  }, []);

  useEffect(() => { if (organizationId && token) void reload(organizationId, token).catch(e => setError(e instanceof Error ? e.message : "بارگذاری CRM ناموفق بود.")); }, [organizationId, token]);

  async function run(action: () => Promise<void>) {
    setBusy(true); setError(""); setNotice("");
    try { await action(); } catch (e) { setError(e instanceof Error ? e.message : "عملیات ناموفق بود."); }
    finally { setBusy(false); }
  }

  async function createContact(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/crm/contacts" + (editingContactId ? "/" + editingContactId : ""), token, { method: editingContactId ? "PUT" : "POST", body: JSON.stringify({ fullName, email, phone, company, notes: contactNotes }) });
      setFullName(""); setEmail(""); setPhone(""); setCompany(""); setContactNotes(""); setEditingContactId(""); await reload(); setNotice(editingContactId ? "اطلاعات مخاطب ویرایش شد." : "مخاطب ثبت شد.");
    });
  }

  function editContact(contact: Contact) {
    setEditingContactId(contact.id); setFullName(contact.fullName); setEmail(contact.email ?? "");
    setPhone(contact.phone ?? ""); setCompany(contact.company ?? ""); setContactNotes(contact.notes ?? "");
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  async function deleteContact(contact: Contact) {
    if (!window.confirm("مخاطب «" + contact.fullName + "» حذف شود؟")) return;
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/crm/contacts/" + contact.id, token, { method: "DELETE" });
      await reload(); setNotice("مخاطب حذف شد.");
    });
  }
  async function createLead(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/crm/leads" + (editingLeadId ? "/" + editingLeadId : ""), token, { method: editingLeadId ? "PUT" : "POST", body: JSON.stringify({ title: leadTitle, contactId: leadContactId || null, estimatedValue: leadValue ? Number(leadValue) : null, source: leadSource, notes: leadNotes }) });
      setLeadTitle(""); setLeadContactId(""); setLeadValue(""); setLeadSource(""); setLeadNotes(""); setEditingLeadId(""); await reload(); setNotice(editingLeadId ? "فرصت فروش ویرایش شد." : "فرصت فروش ثبت شد.");
    });
  }

  function editLead(lead: Lead) {
    setEditingLeadId(lead.id); setLeadTitle(lead.title); setLeadContactId(lead.contactId ?? "");
    setLeadValue(lead.estimatedValue == null ? "" : String(lead.estimatedValue)); setLeadSource(lead.source ?? ""); setLeadNotes(lead.notes ?? "");
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  async function deleteLead(lead: Lead) {
    if (!window.confirm("فرصت فروش «" + lead.title + "» حذف شود؟")) return;
    await run(async () => {
      await request("/api/organizations/" + organizationId + "/crm/leads/" + lead.id, token, { method: "DELETE" });
      await reload(); setNotice("فرصت فروش حذف شد.");
    });
  }
  async function changeStage(lead: Lead, stage: string) {
    await run(async () => {
      await request(`/api/organizations/${organizationId}/crm/leads/${lead.id}/stage`, token, { method: "PATCH", body: JSON.stringify({ stage }) });
      await reload(); setNotice("مرحله فرصت فروش به‌روزرسانی شد.");
    });
  }

  async function createActivity(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await run(async () => {
      await request(`/api/organizations/${organizationId}/crm/activities`, token, { method: "POST", body: JSON.stringify({ activityType, summary: activitySummary, contactId: activityContactId || null }) });
      setActivitySummary(""); setActivityContactId(""); await reload(); setNotice("فعالیت در تاریخچه ثبت شد.");
    });
  }

  if (!token) return <main className="crm-shell"><section className="crm-panel"><h1>ورود به مرکز CRM</h1><p>ابتدا از داشبورد اصلی وارد حساب کاربری شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;

  return <main className="crm-shell" dir="rtl">
    <header className="crm-header"><div><span>MAJAZI / CUSTOMER RELATIONSHIP</span><h1>مدیریت ارتباط با مشتریان</h1><p>مخاطبان، فرصت‌های فروش و تاریخچه تعاملات سازمان</p></div><Link href="/">بازگشت به داشبورد</Link></header>
    <section className="crm-toolbar"><label>فضای سازمانی<select value={organizationId} onChange={e => setOrganizationId(e.target.value)}>{organizations.map(o => <option key={o.id} value={o.id}>{o.name} · {o.role}</option>)}</select></label><button type="button" disabled={busy || !organizationId} onClick={() => void run(async () => { await reload(); setNotice("اطلاعات به‌روزرسانی شد."); })}>به‌روزرسانی</button></section>
    {error && <div className="crm-error" role="alert">{error}</div>}{notice && <div className="crm-notice" role="status">{notice}</div>}
    <section className="crm-stats"><article><span>مخاطبان</span><strong>{contacts.length}</strong></article><article><span>فرصت‌های فروش</span><strong>{leads.length}</strong></article><article><span>فعالیت‌های ثبت‌شده</span><strong>{activities.length}</strong></article><article><span>ارزش فرصت‌ها</span><strong>{leads.reduce((sum, lead) => sum + (lead.estimatedValue ?? 0), 0).toLocaleString("fa-IR")}</strong></article></section>
    <div className="crm-grid">
      <section className="crm-panel"><h2>ثبت مخاطب جدید</h2><form className="crm-form" onSubmit={createContact}><label>نام و نام خانوادگی<input required maxLength={180} value={fullName} onChange={e => setFullName(e.target.value)} /></label><label>ایمیل<input type="email" dir="ltr" value={email} onChange={e => setEmail(e.target.value)} /></label><label>شماره تماس<input dir="ltr" maxLength={40} value={phone} onChange={e => setPhone(e.target.value)} /></label><label>شرکت / مجموعه<input maxLength={180} value={company} onChange={e => setCompany(e.target.value)} /></label><label className="crm-notes-field">یادداشت<textarea maxLength={4000} rows={2} value={contactNotes} onChange={e => setContactNotes(e.target.value)} /></label><div className="crm-form-actions"><button disabled={busy || !organizationId}>{editingContactId ? "ذخیره ویرایش" : "ثبت مخاطب"}</button>{editingContactId && <button type="button" className="crm-cancel" onClick={() => { setEditingContactId(""); setFullName(""); setEmail(""); setPhone(""); setCompany(""); setContactNotes(""); }}>انصراف</button>}</div></form></section>
      <section className="crm-panel"><h2>{editingLeadId ? "ویرایش فرصت فروش" : "ایجاد فرصت فروش"}</h2><form className="crm-form" onSubmit={createLead}><label>عنوان فرصت<input required maxLength={240} value={leadTitle} onChange={e => setLeadTitle(e.target.value)} /></label><label>مخاطب مرتبط<select value={leadContactId} onChange={e => setLeadContactId(e.target.value)}><option value="">بدون مخاطب</option>{contacts.map(c => <option key={c.id} value={c.id}>{c.fullName}</option>)}</select></label><label>ارزش تخمینی<input type="number" min="0" step="0.01" value={leadValue} onChange={e => setLeadValue(e.target.value)} /></label><label>منبع آشنایی<input maxLength={120} value={leadSource} onChange={e => setLeadSource(e.target.value)} placeholder="وب‌سایت، معرفی، تماس..." /></label><label>یادداشت<textarea maxLength={4000} rows={2} value={leadNotes} onChange={e => setLeadNotes(e.target.value)} /></label><div className="crm-form-actions"><button disabled={busy || !organizationId}>{editingLeadId ? "ذخیره ویرایش" : "ثبت فرصت فروش"}</button>{editingLeadId && <button type="button" className="crm-cancel" onClick={() => { setEditingLeadId(""); setLeadTitle(""); setLeadContactId(""); setLeadValue(""); setLeadSource(""); setLeadNotes(""); }}>انصراف</button>}</div></form></section>
    </div>
    <section className="crm-panel crm-table-panel"><div className="crm-panel-heading"><h2>مخاطبان سازمان</h2><span>{contacts.length} مخاطب</span></div>{contacts.length === 0 ? <p className="crm-empty">هنوز مخاطبی ثبت نشده است.</p> : <div className="crm-table-wrap"><table><thead><tr><th>نام</th><th>شرکت</th><th>ایمیل</th><th>تلفن</th><th>عملیات</th></tr></thead><tbody>{contacts.map(c => <tr key={c.id}><td>{c.fullName}</td><td>{c.company || "—"}</td><td dir="ltr">{c.email || "—"}</td><td dir="ltr">{c.phone || "—"}</td><td><div className="crm-row-actions"><button type="button" onClick={() => editContact(c)}>ویرایش</button><button type="button" onClick={() => void deleteContact(c)} disabled={busy}>حذف</button></div></td></tr>)}</tbody></table></div>}</section>
    <section className="crm-panel crm-table-panel"><div className="crm-panel-heading"><h2>قیف فروش</h2><span>{leads.length} فرصت</span></div>{leads.length === 0 ? <p className="crm-empty">هنوز فرصتی ثبت نشده است.</p> : <div className="crm-table-wrap"><table><thead><tr><th>عنوان فرصت</th><th>مخاطب</th><th>ارزش تخمینی</th><th>مرحله</th><th>عملیات</th></tr></thead><tbody>{leads.map(lead => <tr key={lead.id}><td>{lead.title}</td><td>{lead.contactName || "—"}</td><td>{lead.estimatedValue?.toLocaleString("fa-IR") ?? "—"}</td><td><select disabled={busy} value={lead.stage} onChange={e => void changeStage(lead, e.target.value)}>{stages.map(s => <option key={s.id} value={s.id}>{s.label}</option>)}</select></td><td><div className="crm-row-actions"><button type="button" onClick={() => editLead(lead)}>ویرایش</button><button type="button" onClick={() => void deleteLead(lead)} disabled={busy}>حذف</button></div></td></tr>)}</tbody></table></div>}</section>
    <div className="crm-grid">
      <section className="crm-panel"><h2>ثبت تعامل جدید</h2><form className="crm-form" onSubmit={createActivity}><label>نوع تعامل<select value={activityType} onChange={e => setActivityType(e.target.value)}>{activityTypes.map(t => <option key={t.id} value={t.id}>{t.label}</option>)}</select></label><label>مخاطب مرتبط<select value={activityContactId} onChange={e => setActivityContactId(e.target.value)}><option value="">بدون مخاطب</option>{contacts.map(c => <option key={c.id} value={c.id}>{c.fullName}</option>)}</select></label><label>شرح تعامل<textarea required maxLength={1000} value={activitySummary} onChange={e => setActivitySummary(e.target.value)} rows={3} /></label><button disabled={busy || !organizationId}>ثبت در تاریخچه</button></form></section>
      <section className="crm-panel"><h2>آخرین تعاملات</h2>{activities.length === 0 ? <p className="crm-empty">هنوز تعاملی ثبت نشده است.</p> : <div className="crm-activity-list">{activities.slice(0, 12).map(a => <article key={a.id}><b>{activityTypes.find(t => t.id === a.activityType)?.label ?? a.activityType}</b><p>{a.summary}</p><small>{new Date(a.occurredAt).toLocaleString("fa-IR")}</small></article>)}</div>}</section>
    </div>
    <footer className="crm-footer">MAJAZI CRM · داده‌ها به‌صورت مجزا برای هر سازمان نگهداری می‌شوند.</footer>
  </main>;
}
