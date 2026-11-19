"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type Organization = { id: string; name: string; role: string };
type Template = { id: string; module: string; field: string; name: string; content: string; sortOrder: number; isActive: boolean; updatedAt: string };
type Draft = { name: string; content: string; sortOrder: number; isActive: boolean };
const fieldsByModule: Record<string, { value: string; label: string }[]> = {
  crm: [{ value: "contact.notes", label: "یادداشت مخاطب" }, { value: "lead.notes", label: "یادداشت فرصت فروش" }, { value: "activity.summary", label: "شرح تعامل" }],
  campaign: [{ value: "objective", label: "هدف کمپین" }, { value: "notes", label: "یادداشت کمپین" }],
  inbox: [{ value: "conversation.subject", label: "عنوان مکالمه" }, { value: "message.reply", label: "پاسخ پیام" }],
  content: [{ value: "title", label: "عنوان محتوا" }, { value: "body", label: "متن محتوا" }, { value: "description", label: "توضیح محتوا" }],
};
const moduleLabels: Record<string, string> = { crm: "مدیریت ارتباط با مشتری", campaign: "کمپین‌ها", inbox: "صندوق ورودی", content: "استودیوی محتوا" };
const emptyDraft: Draft = { name: "", content: "", sortOrder: 10, isActive: true };

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const token = typeof window !== "undefined" ? localStorage.getItem("majazi_access_token") : null;
  const response = await fetch("/backend-api" + path, { ...init, headers: { "Content-Type": "application/json", ...(token ? { Authorization: "Bearer " + token } : {}), ...init?.headers }, cache: "no-store" });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? data?.detail ?? "درخواست انجام نشد.");
  return data as T;
}

export default function TextTemplatesPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [module, setModule] = useState("crm");
  const [field, setField] = useState("contact.notes");
  const [items, setItems] = useState<Template[]>([]);
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [editingId, setEditingId] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const loadOrganizations = useCallback(async () => {
    try {
      const data = await api<Organization[]>("/api/organizations");
      setOrganizations(data);
      setOrganizationId(current => current && data.some(x => x.id === current) ? current : data[0]?.id ?? "");
    } catch (e) { setError(e instanceof Error ? e.message : "دریافت سازمان‌ها ناموفق بود."); }
    finally { setLoading(false); }
  }, []);

  const loadTemplates = useCallback(async () => {
    if (!organizationId) { setItems([]); return; }
    try {
      const data = await api<Template[]>(`/api/organizations/${organizationId}/text-templates?module=${module}&field=${encodeURIComponent(field)}`);
      setItems(data);
      setError("");
    } catch (e) { setError(e instanceof Error ? e.message : "دریافت قالب‌ها ناموفق بود."); }
  }, [organizationId, module, field]);

  useEffect(() => { void loadOrganizations(); }, [loadOrganizations]);
  useEffect(() => { void loadTemplates(); }, [loadTemplates]);

  function reset() { setEditingId(""); setDraft(emptyDraft); }
  function edit(item: Template) {
    setEditingId(item.id);
    setDraft({ name: item.name, content: item.content, sortOrder: item.sortOrder, isActive: item.isActive });
    setError(""); setNotice("");
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!organizationId) return;
    setSaving(true); setError(""); setNotice("");
    try {
      const path = `/api/organizations/${organizationId}/text-templates${editingId ? `/${editingId}` : ""}`;
      await api(path, { method: editingId ? "PUT" : "POST", body: JSON.stringify(editingId ? draft : { ...draft, module, field }) });
      await loadTemplates();
      setNotice(editingId ? "قالب ویرایش شد." : "قالب جدید ذخیره شد.");
      reset();
    } catch (e) { setError(e instanceof Error ? e.message : "ذخیره قالب ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function deactivate(item: Template) {
    if (!window.confirm(`قالب «${item.name}» غیرفعال شود؟`)) return;
    setSaving(true); setError(""); setNotice("");
    try {
      await api(`/api/organizations/${organizationId}/text-templates/${item.id}`, { method: "DELETE" });
      await loadTemplates();
      setNotice("قالب غیرفعال شد و سابقه آن حفظ شد.");
      if (editingId === item.id) reset();
    } catch (e) { setError(e instanceof Error ? e.message : "غیرفعال‌سازی قالب ناموفق بود."); }
    finally { setSaving(false); }
  }

  return <main dir="rtl" style={{ minHeight: "100vh", background: "#f4f6fb", color: "#172033", padding: "30px clamp(16px,4vw,54px)" }}>
    <div style={{ maxWidth: 1200, margin: "0 auto" }}>
      <a href="/" style={{ color: "#6354c7", textDecoration: "none", fontWeight: 700 }}>← بازگشت به مرکز فرماندهی</a>
      <header style={{ display: "flex", flexWrap: "wrap", alignItems: "end", justifyContent: "space-between", gap: 18, margin: "24px 0" }}>
        <div><span style={{ color: "#7565c9", fontSize: 12, fontWeight: 800, letterSpacing: 1 }}>ORGANIZATION TEXT LIBRARY</span><h1 style={{ margin: "8px 0", fontSize: 30 }}>کتابخانه متن‌های آماده</h1><p style={{ margin: 0, color: "#697386" }}>قالب‌های قابل ویرایش برای CRM، کمپین، صندوق پیام و استودیوی محتوا؛ متن‌ها بعد از درج همچنان قابل ویرایش‌اند.</p></div>
        <label style={{ display: "grid", gap: 7, minWidth: 250, fontSize: 13 }}>فضای کاری<select value={organizationId} onChange={e => setOrganizationId(e.target.value)} style={fieldStyle} disabled={loading || !organizations.length}>{organizations.map(org => <option key={org.id} value={org.id}>{org.name} · {org.role}</option>)}</select></label>
      </header>
      {error && <div role="alert" style={feedbackStyle}>{error}</div>}
      {notice && <div role="status" style={{ ...feedbackStyle, background: "#effbf4", borderColor: "#a7dfc0", color: "#17643a" }}>{notice}</div>}
      <section style={{ ...panelStyle, marginBottom: 18 }}>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit,minmax(210px,1fr))", gap: 14 }}>
          <label style={labelStyle}>ماژول<select value={module} onChange={e => { const next = e.target.value; setModule(next); setField(fieldsByModule[next][0].value); reset(); }} style={fieldStyle}>{Object.entries(moduleLabels).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label>
          <label style={labelStyle}>محل استفاده<select value={field} onChange={e => { setField(e.target.value); reset(); }} style={fieldStyle}>{fieldsByModule[module].map(x => <option key={x.value} value={x.value}>{x.label}</option>)}</select></label>
        </div>
      </section>
      <div style={{ display: "grid", gridTemplateColumns: "minmax(0,1.1fr) minmax(300px,.9fr)", gap: 18, alignItems: "start" }}>
        <section style={panelStyle}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 10, marginBottom: 16 }}><h2 style={{ margin: 0, fontSize: 19 }}>قالب‌های این فیلد</h2><span style={{ color: "#778196", fontSize: 12 }}>{items.filter(x => x.isActive).length} قالب فعال</span></div>
          {loading ? <p>در حال بارگذاری…</p> : items.length === 0 ? <p style={{ color: "#778196", textAlign: "center", padding: 24 }}>قالبی برای این فیلد ثبت نشده است.</p> : <div style={{ display: "grid", gap: 10 }}>
            {items.map(item => <article key={item.id} style={{ border: "1px solid #e8eaf1", borderRadius: 13, padding: 14, opacity: item.isActive ? 1 : .62 }}>
              <div style={{ display: "flex", flexWrap: "wrap", justifyContent: "space-between", gap: 8, alignItems: "center" }}><div><strong>{item.name}</strong><span style={{ marginInlineStart: 8, color: item.isActive ? "#16824d" : "#8b93a3", fontSize: 12 }}>{item.isActive ? "فعال" : "غیرفعال"}</span></div><div style={{ display: "flex", gap: 7 }}><button type="button" style={secondaryButton} onClick={() => edit(item)}>مشاهده و ویرایش</button>{item.isActive && <button type="button" style={dangerButton} disabled={saving} onClick={() => void deactivate(item)}>غیرفعال‌سازی</button>}</div></div>
              <p style={{ whiteSpace: "pre-wrap", lineHeight: 1.9, color: "#5e687a", fontSize: 13, margin: "10px 0 0" }}>{item.content}</p>
            </article>)}
          </div>}
        </section>
        <section style={panelStyle}>
          <h2 style={{ margin: "0 0 6px", fontSize: 19 }}>{editingId ? "ویرایش قالب" : "افزودن قالب جدید"}</h2>
          <p style={{ color: "#778196", fontSize: 13, margin: "0 0 18px" }}>قالب‌های اولیه هنگام اولین مشاهده فیلد به‌طور خودکار ساخته می‌شوند و قابل تغییرند.</p>
          <form onSubmit={save} style={{ display: "grid", gap: 13 }}>
            <label style={labelStyle}>نام قالب<input required maxLength={120} value={draft.name} onChange={e => setDraft(x => ({ ...x, name: e.target.value }))} style={fieldStyle} placeholder="مثلاً پیگیری مشتری جدید" /></label>
            <label style={labelStyle}>متن قالب<textarea required maxLength={8000} rows={9} value={draft.content} onChange={e => setDraft(x => ({ ...x, content: e.target.value }))} style={{ ...fieldStyle, resize: "vertical", lineHeight: 1.9 }} placeholder="متن قابل درج در فرم‌های مرتبط" /></label>
            <label style={labelStyle}>ترتیب نمایش<input type="number" min={0} max={10000} value={draft.sortOrder} onChange={e => setDraft(x => ({ ...x, sortOrder: Number(e.target.value) }))} style={fieldStyle} /></label>
            <label style={{ display: "flex", gap: 9, alignItems: "center", fontSize: 13 }}><input type="checkbox" checked={draft.isActive} onChange={e => setDraft(x => ({ ...x, isActive: e.target.checked }))} /> قالب فعال باشد</label>
            <div style={{ display: "flex", gap: 9, flexWrap: "wrap" }}><button type="submit" disabled={saving || !organizationId} style={primaryButton}>{saving ? "در حال ذخیره…" : editingId ? "ذخیره ویرایش" : "ثبت قالب"}</button>{editingId && <button type="button" onClick={reset} style={secondaryButton}>انصراف</button>}</div>
          </form>
        </section>
      </div>
      <p style={{ color: "#8991a2", fontSize: 12, marginTop: 24 }}>قالب‌ها به سازمان انتخاب‌شده تعلق دارند؛ حذف به‌صورت غیرفعال‌سازی انجام می‌شود تا سوابق حفظ شوند.</p>
    </div>
  </main>;
}

const fieldStyle = { width: "100%", boxSizing: "border-box" as const, border: "1px solid #dfe3ec", borderRadius: 10, background: "#fff", color: "#20283a", padding: "11px 12px", font: "inherit", outlineColor: "#7565c9" };
const panelStyle = { background: "#fff", border: "1px solid #e7e9f0", borderRadius: 18, padding: 20, boxShadow: "0 8px 24px rgba(27,35,59,.04)" };
const labelStyle = { display: "grid", gap: 7, fontSize: 13, fontWeight: 650 as const };
const primaryButton = { border: 0, borderRadius: 10, background: "#6354c7", color: "#fff", padding: "11px 16px", fontWeight: 700, cursor: "pointer" };
const secondaryButton = { border: "1px solid #dfe3ec", borderRadius: 9, background: "#fff", color: "#4c5363", padding: "9px 12px", fontWeight: 650, cursor: "pointer" };
const dangerButton = { border: "1px solid #f1c8c8", borderRadius: 9, background: "#fff7f7", color: "#a43e3e", padding: "9px 12px", fontWeight: 650, cursor: "pointer" };
const feedbackStyle = { border: "1px solid #f0c5c5", borderRadius: 12, background: "#fff4f4", color: "#a33d3d", padding: 12, marginBottom: 16 };
