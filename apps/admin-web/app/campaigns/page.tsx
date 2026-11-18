"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import "./campaigns.css";

type Organization = { id: string; name: string; role: string };
type Brand = { id: string; name: string };
type Campaign = { id: string; brandId?: string | null; name: string; objective?: string | null; channel: string; status: string; budget: number; startsAt?: string | null; endsAt?: string | null; notes?: string | null };
const channels = [{id:"Website",label:"وب‌سایت"},{id:"Instagram",label:"اینستاگرام"},{id:"Telegram",label:"تلگرام"},{id:"Email",label:"ایمیل"},{id:"SMS",label:"پیامک"},{id:"Ads",label:"تبلیغات"},{id:"Offline",label:"محیطی و حضوری"},{id:"Other",label:"سایر"}];
const statuses = [{id:"Draft",label:"پیش‌نویس"},{id:"Scheduled",label:"زمان‌بندی‌شده"},{id:"Active",label:"فعال"},{id:"Paused",label:"متوقف"},{id:"Completed",label:"پایان‌یافته"},{id:"Cancelled",label:"لغوشده"}];
async function request<T>(path:string, token:string, init?:RequestInit):Promise<T>{
 const response=await fetch("/backend-api"+path,{...init,headers:{"Content-Type":"application/json",Authorization:"Bearer "+token,...init?.headers},cache:"no-store"});
 const data=await response.json().catch(()=>null); if(!response.ok) throw new Error(data?.error??"عملیات انجام نشد."); return data as T;
}
const dateValue=(value?:string|null)=>value?new Date(value).toISOString().slice(0,16):"";
export default function CampaignsPage(){
 const [token,setToken]=useState(""); const [organizations,setOrganizations]=useState<Organization[]>([]); const [organizationId,setOrganizationId]=useState("");
 const [brands,setBrands]=useState<Brand[]>([]); const [items,setItems]=useState<Campaign[]>([]);
 const [editingId,setEditingId]=useState(""); const [name,setName]=useState(""); const [objective,setObjective]=useState(""); const [channel,setChannel]=useState("Instagram"); const [status,setStatus]=useState("Draft"); const [budget,setBudget]=useState("0"); const [brandId,setBrandId]=useState(""); const [startsAt,setStartsAt]=useState(""); const [endsAt,setEndsAt]=useState(""); const [notes,setNotes]=useState("");
 const [busy,setBusy]=useState(false); const [error,setError]=useState(""); const [notice,setNotice]=useState("");
 async function reload(org=organizationId,access=token){if(!org||!access)return;const [campaigns,b]=await Promise.all([request<Campaign[]>("/api/organizations/"+org+"/campaigns",access),request<Brand[]>("/api/organizations/"+org+"/brands",access)]);setItems(campaigns);setBrands(b);}
 useEffect(()=>{const saved=localStorage.getItem("majazi_access_token")??"";setToken(saved);if(saved)request<Organization[]>("/api/organizations",saved).then(list=>{setOrganizations(list);setOrganizationId(list[0]?.id??"");}).catch(e=>setError(e instanceof Error?e.message:"دریافت سازمان‌ها ناموفق بود."));},[]);
 useEffect(()=>{if(organizationId&&token)void reload(organizationId,token).catch(e=>setError(e instanceof Error?e.message:"بارگذاری کمپین‌ها ناموفق بود."));},[organizationId,token]);
 function reset(){setEditingId("");setName("");setObjective("");setChannel("Instagram");setStatus("Draft");setBudget("0");setBrandId("");setStartsAt("");setEndsAt("");setNotes("");}
 async function run(action:()=>Promise<void>){setBusy(true);setError("");setNotice("");try{await action();}catch(e){setError(e instanceof Error?e.message:"عملیات ناموفق بود.");}finally{setBusy(false);}}
 async function submit(e:FormEvent<HTMLFormElement>){e.preventDefault();await run(async()=>{const payload={name,objective,channel,status,budget:Number(budget),brandId:brandId||null,startsAt:startsAt?new Date(startsAt).toISOString():null,endsAt:endsAt?new Date(endsAt).toISOString():null,notes};await request("/api/organizations/"+organizationId+"/campaigns"+(editingId?"/"+editingId:""),token,{method:editingId?"PUT":"POST",body:JSON.stringify(payload)});reset();await reload();setNotice(editingId?"کمپین ویرایش شد.":"کمپین ثبت شد.");});}
 function edit(item:Campaign){setEditingId(item.id);setName(item.name);setObjective(item.objective??"");setChannel(item.channel);setStatus(item.status);setBudget(String(item.budget));setBrandId(item.brandId??"");setStartsAt(dateValue(item.startsAt));setEndsAt(dateValue(item.endsAt));setNotes(item.notes??"");window.scrollTo({top:0,behavior:"smooth"});}
 async function remove(item:Campaign){if(!window.confirm("کمپین «"+item.name+"» حذف شود؟"))return;await run(async()=>{await request("/api/organizations/"+organizationId+"/campaigns/"+item.id,token,{method:"DELETE"});await reload();setNotice("کمپین حذف شد.");});}
 if(!token)return <main className="campaign-shell"><section className="campaign-panel"><h1>ورود به مدیریت کمپین</h1><p>ابتدا از داشبورد اصلی وارد حساب کاربری شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;
 const total=items.reduce((sum,x)=>sum+x.budget,0);
 return <main className="campaign-shell" dir="rtl">
  <header className="campaign-header"><div><span>MAJAZI / MARKETING</span><h1>مدیریت کمپین‌های بازاریابی</h1><p>برنامه‌ریزی بودجه، زمان‌بندی و وضعیت اجرای کمپین‌ها</p></div><Link href="/">بازگشت به داشبورد</Link></header>
  <section className="campaign-toolbar"><label>فضای سازمانی<select value={organizationId} onChange={e=>setOrganizationId(e.target.value)}>{organizations.map(o=><option key={o.id} value={o.id}>{o.name} · {o.role}</option>)}</select></label><button type="button" disabled={busy||!organizationId} onClick={()=>void run(async()=>{await reload();setNotice("فهرست کمپین‌ها به‌روزرسانی شد.");})}>به‌روزرسانی</button></section>
  {error&&<div className="campaign-error" role="alert">{error}</div>}{notice&&<div className="campaign-notice" role="status">{notice}</div>}
  <section className="campaign-stats"><article><span>کل کمپین‌ها</span><strong>{items.length.toLocaleString("fa-IR")}</strong></article><article><span>کمپین‌های فعال</span><strong>{items.filter(x=>x.status==="Active").length.toLocaleString("fa-IR")}</strong></article><article><span>بودجه برنامه‌ریزی‌شده</span><strong>{total.toLocaleString("fa-IR")}</strong></article></section>
  <section className="campaign-panel"><h2>{editingId?"ویرایش کمپین":"تعریف کمپین جدید"}</h2><form className="campaign-form" onSubmit={submit}>
   <label>نام کمپین<input required maxLength={200} value={name} onChange={e=>setName(e.target.value)} placeholder="مثلاً جشنواره فروش پاییزه"/></label>
   <label>کانال اجرا<select value={channel} onChange={e=>setChannel(e.target.value)}>{channels.map(x=><option key={x.id} value={x.id}>{x.label}</option>)}</select></label>
   <label>برند مرتبط<select value={brandId} onChange={e=>setBrandId(e.target.value)}><option value="">بدون برند مشخص</option>{brands.map(x=><option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
   <label>وضعیت<select value={status} onChange={e=>setStatus(e.target.value)}>{statuses.map(x=><option key={x.id} value={x.id}>{x.label}</option>)}</select></label>
   <label>بودجه<input type="number" min="0" step="0.01" required value={budget} onChange={e=>setBudget(e.target.value)}/></label>
   <label>شروع برنامه<input type="datetime-local" value={startsAt} onChange={e=>setStartsAt(e.target.value)}/></label>
   <label>پایان برنامه<input type="datetime-local" value={endsAt} onChange={e=>setEndsAt(e.target.value)}/></label>
   <label className="campaign-wide">هدف کمپین<input maxLength={1000} value={objective} onChange={e=>setObjective(e.target.value)} placeholder="هدف و نتیجه مورد انتظار"/></label>
   <label className="campaign-wide">یادداشت<textarea rows={2} maxLength={4000} value={notes} onChange={e=>setNotes(e.target.value)}/></label>
   <div className="campaign-actions"><button disabled={busy||!organizationId}>{editingId?"ذخیره تغییرات":"ثبت کمپین"}</button>{editingId&&<button type="button" className="campaign-cancel" onClick={reset}>انصراف</button>}</div>
  </form></section>
  <section className="campaign-panel"><div className="campaign-heading"><h2>فهرست کمپین‌ها</h2><span>{items.length.toLocaleString("fa-IR")} مورد</span></div>{items.length===0?<p className="campaign-empty">هنوز کمپینی برای این سازمان ثبت نشده است.</p>:<div className="campaign-table-wrap"><table><thead><tr><th>نام کمپین</th><th>کانال</th><th>بودجه</th><th>بازه اجرا</th><th>وضعیت</th><th>عملیات</th></tr></thead><tbody>{items.map(x=><tr key={x.id}><td><strong>{x.name}</strong>{x.objective&&<small>{x.objective}</small>}</td><td>{channels.find(c=>c.id===x.channel)?.label??x.channel}</td><td>{x.budget.toLocaleString("fa-IR")}</td><td>{x.startsAt?new Date(x.startsAt).toLocaleDateString("fa-IR"):"—"} تا {x.endsAt?new Date(x.endsAt).toLocaleDateString("fa-IR"):"—"}</td><td><span className={"campaign-status status-"+x.status.toLowerCase()}>{statuses.find(s=>s.id===x.status)?.label??x.status}</span></td><td><div className="campaign-row-actions"><button type="button" onClick={()=>edit(x)}>ویرایش</button><button type="button" disabled={busy} onClick={()=>void remove(x)}>حذف</button></div></td></tr>)}</tbody></table></div>}</section>
  <footer>MAJAZI CAMPAIGNS · اطلاعات هر کمپین در محدوده سازمان مربوطه نگهداری می‌شود.</footer>
 </main>;
}
