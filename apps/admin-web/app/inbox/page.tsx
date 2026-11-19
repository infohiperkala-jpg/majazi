"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import "./inbox.css";

type Organization = { id: string; name: string; role: string };
type InboxAssignee = { id: string; email: string; role: string };
type Conversation = {
  id: string; channelConnectionId?: string | null; contactId?: string | null;
  subject?: string | null; status: string; assignedToUserId?: string | null;
  lastMessageAt: string; channel?: string | null; channelName?: string | null;
  contactName?: string | null; messageCount: number; lastMessage?: string | null;
  lastDirection?: string | null;
};
type Message = { id: string; direction: string; body: string; status: string; createdAt: string; createdByUserId?: string | null };
type StatusOption = { id: string; key: string; label: string; color: string; isActive: boolean; sortOrder: number };
type TextTemplate = { id: string; module: string; field: string; name: string; content: string; isActive: boolean; sortOrder: number };
const labels: Record<string,string> = { Open: "باز", Pending: "در انتظار پاسخ", Resolved: "بسته‌شده", open: "باز", pending: "در انتظار پاسخ", resolved: "بسته‌شده" };
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
  const [statusOptions,setStatusOptions] = useState<StatusOption[]>([]);
  const [assignees,setAssignees] = useState<InboxAssignee[]>([]);
  const [replyTemplates,setReplyTemplates] = useState<TextTemplate[]>([]);
  const [selected,setSelected] = useState("");
  const [messages,setMessages] = useState<Message[]>([]);
  const [status,setStatus] = useState("All");
  const [assignment,setAssignment] = useState("all");
  const [search,setSearch] = useState("");
  const [reply,setReply] = useState("");
  const [busy,setBusy] = useState(false);
  const [error,setError] = useState("");
  const [notice,setNotice] = useState("");
  async function loadConversations(org=organizationId, access=token) {
    if (!org || !access) return;
    const [result, configuredStatuses, templates, team] = await Promise.all([
      api<{items:Conversation[];total:number}>("/api/organizations/"+org+"/inbox/conversations?status="+encodeURIComponent(status)+"&q="+encodeURIComponent(search)+"&assignment="+encodeURIComponent(assignment==="all"?"":assignment),access),
      api<StatusOption[]>("/api/organizations/"+org+"/statuses?module=inbox.conversation",access),
      api<TextTemplate[]>("/api/organizations/"+org+"/text-templates?module=inbox",access),
      api<InboxAssignee[]>("/api/organizations/"+org+"/inbox/assignees",access)
    ]);
    setItems(result.items);
    setAssignees(team);
    setStatusOptions(configuredStatuses.filter(x=>x.isActive).sort((a,b)=>a.sortOrder-b.sortOrder));
    setReplyTemplates(templates.filter(x=>x.isActive&&x.field==="message.reply").sort((a,b)=>a.sortOrder-b.sortOrder));
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
  useEffect(()=>{if(organizationId&&token)loadConversations(organizationId,token).catch(e=>setError(e instanceof Error?e.message:"بارگذاری صندوق ورودی ناموفق بود."));},[organizationId,token,status,assignment]);
  useEffect(()=>{if(selected&&organizationId&&token)loadMessages(selected,organizationId,token).catch(e=>setError(e instanceof Error?e.message:"دریافت پیام‌ها ناموفق بود."));},[selected,organizationId,token]);
  async function run(action:()=>Promise<void>) {
    setBusy(true);setError("");setNotice("");
    try{await action();}catch(e){setError(e instanceof Error?e.message:"عملیات ناموفق بود.");}
    finally{setBusy(false);}
  }
  async function generateReply() {
    const conversation = items.find(x=>x.id===selected);
    if (!conversation || !organizationId || !token) return;
    await run(async()=>{
      const latestInbound = [...messages].reverse().find(x=>x.direction==="Inbound")?.body ?? conversation.lastMessage ?? "";
      const result = await api<{text:string}>("/api/organizations/"+organizationId+"/ai/generate-text",token,{
        method:"POST",
        body:JSON.stringify({
          module:"inbox", field:"message.reply",
          context:"مخاطب: "+(conversation.contactName||"مخاطب")+"؛ عنوان مکالمه: "+(conversation.subject||"بدون عنوان")+"؛ آخرین پیام دریافتی: "+latestInbound,
          currentText:reply,
          instructions:"یک پاسخ کوتاه، مودبانه، دقیق و متناسب با آخرین پیام بنویس. اگر اطلاعات لازم در مکالمه نیست، قول یا اطلاعات ساختگی نده و سؤال روشن‌کننده پیشنهاد کن."
        })
      });
      setReply(result.text);
      setNotice("پاسخ پیشنهادی آماده شد؛ متن را بررسی و در صورت تأیید ارسال کنید.");
    });
  }

  async function send(e:FormEvent<HTMLFormElement>) {
    e.preventDefault();
    await run(async()=>{
      const result=await api<{status:string;deliveryNote:string}>("/api/organizations/"+organizationId+"/inbox/conversations/"+selected+"/messages",token,{method:"POST",body:JSON.stringify({body:reply})});
      setReply("");await loadMessages();await loadConversations();setNotice(result.deliveryNote || (result.status==="Sent"?"پیام ارسال شد.":"پیام در صف داخلی ثبت شد."));
    });
  }
  async function createConversation() {
    const subject=window.prompt("عنوان مکالمه جدید را وارد کنید:");
    if(subject===null||!subject.trim())return;
    await run(async()=>{
      const created=await api<{id:string}>("/api/organizations/"+organizationId+"/inbox/conversations",token,{method:"POST",body:JSON.stringify({subject:subject.trim()})});
      await loadConversations();setSelected(created.id);setMessages([]);setNotice("مکالمه جدید ایجاد شد.");
    });
  }
  async function assignConversation(next:string) {
    if (!selected) return;
    await run(async()=>{
      await api("/api/organizations/"+organizationId+"/inbox/conversations/"+selected,token,{
        method:"PATCH",body:JSON.stringify({assignedToUserId:next||"00000000-0000-0000-0000-000000000000"})
      });
      await loadConversations();setNotice(next?"مسئول مکالمه تعیین شد.":"مسئولیت مکالمه برداشته شد.");
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
      <aside className="inbox-list-panel"><div className="inbox-list-heading"><h2>مکالمات</h2><div><span>{items.length.toLocaleString("en-US")}</span><button type="button" className="inbox-new-button" disabled={busy||!organizationId} onClick={()=>void createConversation()}>مکالمه جدید +</button></div></div><div className="inbox-filters">{[["All","همه"],...statusOptions.map(x=>[x.key,x.label])].map(([id,label])=><button key={id} className={status===id?"active":""} onClick={()=>setStatus(id)}>{label}</button>)}</div><div className="inbox-assignment-filters" aria-label="فیلتر مسئول مکالمه">{[["all","همه مسئول‌ها"],["me","مکالمات من"],["unassigned","بدون مسئول"]].map(([id,label])=><button type="button" key={id} className={assignment===id?"active":""} onClick={()=>setAssignment(id)}>{label}</button>)}</div>
        {items.length===0?<div className="inbox-empty-state"><strong>مکالمه‌ای وجود ندارد</strong><p>با دریافت پیام از کانال‌ها یا ایجاد مکالمه، گفتگوها اینجا نمایش داده می‌شوند.</p></div>:items.map(item=><button key={item.id} className={"inbox-conversation "+(selected===item.id?"selected":"")} onClick={()=>setSelected(item.id)}>
          <div className="inbox-conversation-top"><strong>{item.contactName||item.subject||item.channelName||"مکالمه بدون عنوان"}</strong><time>{new Date(item.lastMessageAt).toLocaleDateString("en-US")}</time></div>
          <div className="inbox-conversation-meta"><span>{item.channel||"دستی"}{item.channelName?" · "+item.channelName:""} · {assignees.find(x=>x.id===item.assignedToUserId)?.email||"بدون مسئول"}</span><span className={"inbox-status status-"+item.status.toLowerCase()}>{statusOptions.find(x=>x.key.toLowerCase()===item.status.toLowerCase())?.label??labels[item.status]??item.status}</span></div>
          <p>{item.lastDirection==="Outbound"?"شما: ":""}{item.lastMessage||"هنوز پیامی ثبت نشده است."}</p>
        </button>)}
      </aside>
      <section className="inbox-chat-panel">
        {active?<><div className="inbox-chat-header"><div><span>گفتگو</span><h2>{active.contactName||active.subject||"مکالمه بدون عنوان"}</h2><small>{active.channelName||active.channel||"کانال نامشخص"} · {active.messageCount.toLocaleString("en-US")} پیام · {assignees.find(x=>x.id===active.assignedToUserId)?.email||"بدون مسئول"}</small></div><div className="inbox-conversation-controls"><label>وضعیت<select value={active.status.toLowerCase()} onChange={e=>void changeStatus(e.target.value)} disabled={busy}>{statusOptions.map(x=><option key={x.id} value={x.key}>{x.label}</option>)}</select></label><label>مسئول مکالمه<select value={active.assignedToUserId||""} onChange={e=>void assignConversation(e.target.value)} disabled={busy}><option value="">بدون مسئول</option>{assignees.map(x=><option key={x.id} value={x.id}>{x.email} · {x.role}</option>)}</select></label></div></div>
          <div className="inbox-messages">{messages.length===0?<p className="inbox-empty-state">هنوز پیامی برای این مکالمه ثبت نشده است.</p>:messages.map(message=><article key={message.id} className={"inbox-message "+(message.direction==="Outbound"?"outbound":"inbound")}><p>{message.body}</p><footer><time>{new Date(message.createdAt).toLocaleString("en-US")}</time><span>{message.direction==="Outbound"?"ارسالی · "+(message.status==="Queued"?"در صف داخلی":message.status):"دریافتی"}</span></footer></article>)}</div>
          <form className="inbox-reply" onSubmit={send}><select value="" onChange={e=>{const t=replyTemplates.find(x=>x.id===e.target.value);if(t)setReply(t.content);}}><option value="">درج پاسخ آماده…</option>{replyTemplates.map(x=><option key={x.id} value={x.id}>{x.name}</option>)}</select><textarea value={reply} onChange={e=>setReply(e.target.value)} maxLength={10000} placeholder="پاسخ اپراتور را بنویسید..." required/><div className="inbox-reply-actions"><button type="button" disabled={busy||active.status.toLowerCase()==="resolved"} onClick={()=>void generateReply()}>پیشنهاد پاسخ با هوش مصنوعی</button><button disabled={busy||!reply.trim()||active.status.toLowerCase()==="resolved"}>ارسال پاسخ</button></div><small>برای مکالمه تلگرام متصل، ارسال از طریق Bot API انجام می‌شود؛ سایر کانال‌ها تا اتصال ارسال‌گر رسمی در صف داخلی می‌مانند.</small></form>
        </>:<div className="inbox-select-prompt"><div>✉</div><h2>یک مکالمه انتخاب کنید</h2><p>برای مشاهده تاریخچه پیام‌ها، مکالمه‌ای را از فهرست انتخاب کنید.</p></div>}
      </section>
    </div>
    <footer className="inbox-footer">MAJAZI INBOX · اطلاعات به‌صورت مجزا برای هر سازمان نگهداری می‌شود.</footer>
  </main>;
}
