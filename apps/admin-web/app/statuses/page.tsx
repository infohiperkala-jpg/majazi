"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";

type Organization = { id: string; name: string; role: string };
type StatusItem = { id: string; module: string; key: string; label: string; color: string; description: string | null; sortOrder: number; isActive: boolean; updatedAt: string };
type Draft = { module: string; key: string; label: string; color: string; description: string; sortOrder: number; isActive: boolean };
const modules = [
  { value: "crm.lead", label: "مراحل سرنخ فروش" },
  { value: "campaign", label: "وضعیت کمپین" },
];
const emptyDraft: Draft = { module: "crm.lead", key: "", label: "", color: "#64748b", description: "", sortOrder: 0, isActive: true };

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const token = typeof window !== "undefined" ? localStorage.getItem("majazi_access_token") : null;
  const response = await fetch(`/backend-api${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}), ...init?.headers },
    cache: "no-store",
  });
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? (response.status === 403 ? "فقط مالک یا مدیر سازمان اجازه مدیریت وضعیت‌ها را دارد." : "درخواست انجام نشد."));
  return body as T;
}

export default function StatusManagementPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [items, setItems] = useState<StatusItem[]>([]);
  const [module, setModule] = useState("crm.lead");
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

  const loadStatuses = useCallback(async () => {
    if (!organizationId) { setItems([]); return; }
    try {
      const data = await api<StatusItem[]>(`/api/organizations/${organizationId}/statuses?module=${encodeURIComponent(module)}`);
      setItems(data);
      setError("");
    } catch (e) { setError(e instanceof Error ? e.message : "دریافت وضعیت‌ها ناموفق بود."); }
  }, [organizationId, module]);

  useEffect(() => { void loadOrganizations(); }, [loadOrganizations]);
  useEffect(() => { void loadStatuses(); }, [loadStatuses]);

  function startEdit(item: StatusItem) {
    setEditingId(item.id);
    setDraft({ module: item.module, key: item.key, label: item.label, color: item.color, description: item.description ?? "", sortOrder: item.sortOrder, isActive: item.isActive });
    setError(""); setNotice("");
  }
  function resetForm() { setEditingId(""); setDraft({ ...emptyDraft, module }); }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!organizationId) return;
    setSaving(true); setError(""); setNotice("");
    try {
      const path = `/api/organizations/${organizationId}/statuses${editingId ? `/${editingId}` : ""}`;
      await api(path, { method: editingId ? "PUT" : "POST", body: JSON.stringify(draft) });
      await loadStatuses();
      setNotice(editingId ? "وضعیت با موفقیت ویرایش شد." : "وضعیت جدید در پایگاه داده ثبت شد.");
      resetForm();
    } catch (e) { setError(e instanceof Error ? e.message : "ذخیره وضعیت ناموفق بود."); }
    finally { setSaving(false); }
  }

  async function deactivate(item: StatusItem) {
    if (!window.confirm(`وضعیت «${item.label}» غیرفعال شود؟`)) return;
    setSaving(true); setError(""); setNotice("");
    try {
      await api(`/api/organizations/${organizationId}/statuses/${item.id}`, { method: "DELETE" });
      await loadStatuses();
      setNotice("وضعیت غیرفعال شد؛ سابقه آن در پایگاه داده باقی می‌ماند.");
      if (editingId === item.id) resetForm();
    } catch (e) { setError(e instanceof Error ? e.message : "غیرفعال‌سازی وضعیت ناموفق بود."); }
    finally { setSaving(false); }
  }

  return <main dir="rtl" style={{ minHeight: "100vh", background: "var(--page, #f4f6fb)", color: "var(--ink, #172033)", padding: "32px clamp(16px, 4vw, 56px)" }}>
    <div style={{ maxWidth: 1180, margin: "0 auto" }}>
      <a href="/" style={{ color: "#6756c9", textDecoration: "none", fontWeight: 700 }}>← بازگشت به مرکز فرماندهی</a>
      <header style={{ display: "flex", flexWrap: "wrap", alignItems: "end", justifyContent: "space-between", gap: 18, margin: "24px 0" }}>
        <div><span style={{ color: "#7565c9", fontSize: 12, fontWeight: 800, letterSpacing: 1 }}>WORKSPACE CONFIGURATION</span><h1 style={{ margin: "8px 0", fontSize: 30 }}>مدیریت وضعیت‌ها</h1><p style={{ margin: 0, color: "#697386" }}>تعریف، مرتب‌سازی و فعال یا غیرفعال کردن وضعیت‌های هر ماژول برای سازمان انتخاب‌شده.</p></div>
        <label style={{ display: "grid", gap: 7, minWidth: 250, fontSize: 13 }}>فضای کاری
          <select value={organizationId} onChange={e => setOrganizationId(e.target.value)} style={fieldStyle} disabled={loading || !organizations.length}>
            {organizations.map(org => <option key={org.id} value={org.id}>{org.name}</option>)}
          </select>
        </label>
      </header>
      {error && <div role="alert" style={feedbackStyle}>{error}</div>}
      {notice && <div role="status" style={{ ...feedbackStyle, borderColor: "#a7dfc0", background: "#effbf4", color: "#17643a" }}>{notice}</div>}
      <div style={{ display: "grid", gridTemplateColumns: "minmax(0, 1.2fr) minmax(300px, .8fr)", gap: 20, alignItems: "start" }}>
        <section style={panelStyle}>
          <div style={{ display: "flex", flexWrap: "wrap", gap: 12, justifyContent: "space-between", alignItems: "center", marginBottom: 18 }}>
            <h2 style={{ margin: 0, fontSize: 19 }}>فهرست وضعیت‌ها</h2>
            <select value={module} onChange={e => { setModule(e.target.value); setEditingId(""); setDraft({ ...emptyDraft, module: e.target.value }); }} style={{ ...fieldStyle, width: "auto", minWidth: 190 }}>
              {modules.map(x => <option key={x.value} value={x.value}>{x.label}</option>)}
            </select>
          </div>
          {loading ? <p>در حال بارگذاری…</p> : items.length === 0 ? <p style={{ color: "#778196", padding: "28px 0", textAlign: "center" }}>برای این ماژول هنوز وضعیتی ثبت نشده است.</p> :
            <div style={{ display: "grid", gap: 10 }}>
              {items.map(item => <article key={item.id} style={{ display: "flex", flexWrap: "wrap", gap: 12, alignItems: "center", border: "1px solid #e8eaf1", borderRadius: 14, padding: 14, background: "#fff" }}>
                <span style={{ width: 12, height: 36, borderRadius: 8, background: item.color, opacity: item.isActive ? 1 : .35 }} />
                <div style={{ flex: "1 1 180px" }}><strong>{item.label}</strong><div style={{ color: "#81899a", fontSize: 12, marginTop: 4 }}><code>{item.key}</code> · ترتیب {item.sortOrder} · {item.isActive ? "فعال" : "غیرفعال"}</div>{item.description && <p style={{ color: "#667085", fontSize: 13, margin: "6px 0 0" }}>{item.description}</p>}</div>
                <button type="button" onClick={() => startEdit(item)} style={secondaryButton}>مشاهده و ویرایش</button>
                {item.isActive && <button type="button" disabled={saving} onClick={() => void deactivate(item)} style={dangerButton}>غیرفعال‌سازی</button>}
              </article>)}
            </div>}
        </section>
        <section style={panelStyle}>
          <h2 style={{ margin: "0 0 6px", fontSize: 19 }}>{editingId ? "ویرایش وضعیت" : "ایجاد وضعیت جدید"}</h2>
          <p style={{ color: "#778196", fontSize: 13, margin: "0 0 18px" }}>کلید فنی پس از ایجاد ثابت می‌ماند تا اتصال‌های بعدی به رکوردها پایدار باشد.</p>
          <form onSubmit={save} style={{ display: "grid", gap: 13 }}>
            <label style={labelStyle}>عنوان نمایشی<input required maxLength={100} value={draft.label} onChange={e => setDraft(x => ({ ...x, label: e.target.value }))} style={fieldStyle} placeholder="مثلاً در حال پیگیری" /></label>
            <label style={labelStyle}>کلید یکتا<input required disabled={!!editingId} pattern="[a-z0-9][a-z0-9._-]{0,63}" maxLength={64} value={draft.key} onChange={e => setDraft(x => ({ ...x, key: e.target.value.toLowerCase() }))} style={fieldStyle} dir="ltr" placeholder="مثلاً follow_up" /></label>
            <label style={labelStyle}>توضیحات<textarea maxLength={500} rows={3} value={draft.description} onChange={e => setDraft(x => ({ ...x, description: e.target.value }))} style={{ ...fieldStyle, resize: "vertical" }} placeholder="شرح کاربرد این وضعیت" /></label>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <label style={labelStyle}>رنگ وضعیت<input type="color" value={draft.color} onChange={e => setDraft(x => ({ ...x, color: e.target.value }))} style={{ ...fieldStyle, height: 44, padding: 4 }} /></label>
              <label style={labelStyle}>ترتیب نمایش<input type="number" min={0} max={10000} value={draft.sortOrder} onChange={e => setDraft(x => ({ ...x, sortOrder: Number(e.target.value) }))} style={fieldStyle} /></label>
            </div>
            <label style={{ display: "flex", gap: 9, alignItems: "center", fontSize: 13 }}><input type="checkbox" checked={draft.isActive} onChange={e => setDraft(x => ({ ...x, isActive: e.target.checked }))} /> وضعیت فعال باشد</label>
            <div style={{ display: "flex", gap: 9, flexWrap: "wrap" }}><button type="submit" disabled={saving || !organizationId} style={primaryButton}>{saving ? "در حال ذخیره…" : editingId ? "ذخیره ویرایش" : "ثبت وضعیت"}</button>{editingId && <button type="button" onClick={resetForm} style={secondaryButton}>انصراف</button>}</div>
          </form>
        </section>
      </div>
      <p style={{ color: "#8991a2", fontSize: 12, marginTop: 24 }}>تغییرات در محدوده سازمان ذخیره می‌شوند. حذف به‌صورت غیرفعال‌سازی است تا سوابق و قابلیت بازیابی حفظ شود.</p>
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
