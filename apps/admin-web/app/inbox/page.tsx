"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import "./inbox.css";

type Organization = { id: string; name: string; role: string };
type Conversation = {
  id: string; channelConnectionId?: string | null; contactId?: string | null;
  subject?: string | null; status: string; assignedToUserId?: string | null;
  lastMessageAt: string; channel?: string | null; channelName?: string | null;
  contactName?: string | null; messageCount: number; lastMessage?: string | null;
  lastDirection?: string | null;
};
type Message = { id: string; direction: string; body: string; status: string; createdAt: string; createdByUserId?: string | null };
const labels: Record<string,string> = { Open: "باز", Pending: "در انتظار پاسخ", Resolved: "بسته‌شده" };
async function api<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const response = await fetch("/backend-api" + path, {
    ...init, headers: { "Content-Type": "application/json", Authorization: "Bearer " + token, ...init?.headers },
    cache: "no-store"
  });
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new Error(data?.error ?? "درخواست ناموفق بود.");
  return data as T;
}
export default function InboxPage() {
  const [token,setToken] = useState("");
  const [organizations,setOrganizations] = useState<Organization[]>([]);
  const [organizationId,setOrganizationId] = useState("");
  const [items,setItems] = useState<Conversation[]>([]);
  const [selected,setSelected] = useState("");
  const [messages,setMessages] = useState<Message[]>([]);
  const [status,setStatus] = useState("All");
  const [search,setSearch] = useState("");
  const [reply,setReply] = useState("");
  const [busy,setBusy] = useState(false);
  const [error,setError] = useState("");
  const [notice,setNotice] = useState("");
  async function loadConversations(org=organizationId, access=token) {
    if (!org || !access) return;
    const result = await api<{items:Conversation[]}>("/api/organizations/"+org+"/inbox/conversations?status="+encodeURIComponent(status)+"&q="+encodeURIComponent(search),access);
    setItems(result.items);
    if (selected && !result.items.some(x=>x.id===selected)) { setSelected(""); setMessages([]); }
  }
  async function loadMessages(id=selected, org=organizationId, access=token) {
    if (!id || !org || !access) return;
    setMessages(await api<Message[]>("/api/organizations/"+org+"/inbox/conversations/"+id+"/messages",access));
  }
  useEffect(()=>{
    const saved=localStorage.getItem("majazi_access_token")??"";
    setToken(saved);
    if(saved) api<Organization[]>("/api/organizations",saved).then(list=>{setOrganizations(list);setOrganizationId(list[0]?.id??"");})
      .catch(e=>setError(e instanceof Error?e.message:"دریافت سازمان‌ها ناموفق بود."));
  },[]);
  useEffect(()=>{if(organizationId&&token)loadConversations(organizationId,token).catch(e=>setError(e instanceof Error?e.message:"بارگذاری صندوق ورودی ناموفق بود."));},[organizationId,token,status]);
  useEffect(()=>{if(selected&&organizationId&&token)loadMessages(selected,organizationId,token).catch(e=>setError(e instanceof Error?e.message:"دریافت پیام‌ها ناموفق بود."));},[selected,organizationId,token]);
  async function run(action:()=>Promise<void>) {
    setBusy(true);setError("");setNotice("");
    try{await action();}catch(e){setError(e instanceof Error?e.message:"عملیات ناموفق بود.");}
    finally{setBusy(false);}
  }
  async function send(e:FormEvent<HTMLFormElement>) {
    e.preventDefault();
    await run(async()=>{
      await api("/api/organizations/"+organizationId+"/inbox/conversations/"+selected+"/messages",token,{method:"POST",body:JSON.stringify({body:reply})});
      setReply("");await loadMessages();await loadConversations();setNotice("پیام در صف داخلی ثبت شد؛ هنوز به پیام‌رسان ارسال نشده است.");
    });
  }
  async function changeStatus(next:string) {
    await run(async()=>{
      await api("/api/organizations/"+organizationId+"/inbox/conversations/"+selected,token,{method:"PATCH",body:JSON.stringify({status:next})});
      await loadConversations();setNotice("وضعیت مکالمه به‌روزرسانی شد.");
    });
  }
  if(!token)return <main className="inbox-shell"><section className="inbox-empty"><h1>صندوق ورودی MAJAZI</h1><p>برای مشاهده مکالمات ابتدا وارد پنل شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;
  const active=items.find(x=>x.id===selected);
  return <main className="inbox-shell" dir="rtl">
    <header className="inbox-header"><div><span>MAJAZI / CUSTOMER MESSAGING</span><h1>صندوق ورودی یکپارچه</h1><p>مدیریت مکالمات ثبت‌شده در فضای سازمانی</p></div><Link href="/">بازگشت به داشبورد</Link></header>
    <div className="inbox-toolbar"><label>سازمان<select value={organizationId} onChange={e=>{setOrganizationId(e.target.value);setSelected("");}}>{organizations.map(o=><option key={o.id} value={o.id}>{o.name} · {o.role}</option>)}</select></label><label>جست‌وجوی مکالمه<input value={search} onChange={e=>setSearch(e.target.value)} onKeyDown={e=>{if(e.key==="Enter")void run(()=>loadConversations());}} placeholder="نام مخاطب یا متن پیام"/></label><button disabled={busy} onClick={()=>void run(()=>loadConversations())}>جست‌وجو</button></div>
    {error&&<div className="inbox-alert" role="alert">{error}</div>}{notice&&<div className="inbox-notice" role="status">{notice}</div>}
    <div className="inbox-layout">
      <aside className="inbox-list-panel"><div className="inbox-list-heading"><h2>مکالمات</h2><span>{items.length.toLocaleString("en-US")}</span></div><div className="inbox-filters">{[["All","همه"],["Open","باز"],["Pending","در انتظار"],["Resolved","بسته‌شده"]].map(([id,label])=><button key={id} className={status===id?"active":""} onClick={()=>setStatus(id)}>{label}</button>)}</div>
        {items.length===0?<div className="inbox-empty-state"><strong>مکالمه‌ای وجود ندارد</strong><p>با دریافت پیام از کانال‌ها یا ایجاد مکالمه، گفتگوها اینجا نمایش داده می‌شوند.</p></div>:items.map(item=><button key={item.id} className={"inbox-conversation "+(selected===item.id?"selected":"")} onClick={()=>setSelected(item.id)}>
          <div className="inbox-conversation-top"><strong>{item.contactName||item.subject||item.channelName||"مکالمه بدون عنوان"}</strong><time>{new Date(item.lastMessageAt).toLocaleDateString("en-US")}</time></div>
          <div className="inbox-conversation-meta"><span>{item.channel||"دستی"}{item.channelName?" · "+item.channelName:""}</span><span className={"inbox-status status-"+item.status.toLowerCase()}>{labels[item.status]??item.status}</span></div>
          <p>{item.lastDirection==="Outbound"?"شما: ":""}{item.lastMessage||"هنوز پیامی ثبت نشده است."}</p>
        </button>)}
      </aside>
      <section className="inbox-chat-panel">
        {active?<><div className="inbox-chat-header"><div><span>گفتگو</span><h2>{active.contactName||active.subject||"مکالمه بدون عنوان"}</h2><small>{active.channelName||active.channel||"کانال نامشخص"} · {active.messageCount.toLocaleString("en-US")} پیام</small></div><select value={active.status} onChange={e=>void changeStatus(e.target.value)} disabled={busy}><option value="Open">باز</option><option value="Pending">در انتظار پاسخ</option><option value="Resolved">بسته‌شده</option></select></div>
          <div className="inbox-messages">{messages.length===0?<p className="inbox-empty-state">هنوز پیامی برای این مکالمه ثبت نشده است.</p>:messages.map(message=><article key={message.id} className={"inbox-message "+(message.direction==="Outbound"?"outbound":"inbound")}><p>{message.body}</p><footer><time>{new Date(message.createdAt).toLocaleString("en-US")}</time><span>{message.direction==="Outbound"?"ارسالی · "+(message.status==="Queued"?"در صف داخلی":message.status):"دریافتی"}</span></footer></article>)}</div>
          <form className="inbox-reply" onSubmit={send}><textarea value={reply} onChange={e=>setReply(e.target.value)} maxLength={10000} placeholder="پاسخ اپراتور را بنویسید..." required/><button disabled={busy||!reply.trim()||active.status==="Resolved"}>ثبت پاسخ در صف</button><small>فعلاً پیام فقط در سامانه ذخیره می‌شود؛ ارسال به پیام‌رسان پس از راه‌اندازی ارسال‌گر رسمی فعال خواهد شد.</small></form>
        </>:<div className="inbox-select-prompt"><div>✉</div><h2>یک مکالمه انتخاب کنید</h2><p>برای مشاهده تاریخچه پیام‌ها، مکالمه‌ای را از فهرست انتخاب کنید.</p></div>}
      </section>
    </div>
    <footer className="inbox-footer">MAJAZI INBOX · اطلاعات به‌صورت مجزا برای هر سازمان نگهداری می‌شود.</footer>
  </main>;
}
