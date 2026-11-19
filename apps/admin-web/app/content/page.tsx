"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import "./content.css";

type Organization={id:string;name:string;role:string};
type Brand={id:string;name:string};
type Draft={id:string;scheduledAt?:string|null;publishedAt?:string|null;publishError?:string|null;brandId?:string|null;title:string;content:string;channel:string;tone:string;status:string;aiModel?:string|null;createdAt:string;updatedAt:string;approvedAt?:string|null;versionCount?:number;versions?:Version[]};
type Version={id:string;versionNumber:number;title:string;content:string;changeNote:string;createdAt:string};
type AIStatus={configured:boolean;model:string;provider:string;keyStored:boolean};
type PublishTarget={id:string;displayName:string;externalAccountId?:string|null};
async function api<T>(path:string,token:string,init?:RequestInit):Promise<T>{
 const response=await fetch("/backend-api"+path,{...init,headers:{"Content-Type":"application/json",Authorization:"Bearer "+token,...init?.headers},cache:"no-store"});
 const data=await response.json().catch(()=>null);
 if(!response.ok)throw new Error(data?.error??data?.detail??"درخواست ناموفق بود.");
 return data as T;
}
const statuses:Record<string,string>={Draft:"پیش‌نویس",Approved:"تأییدشده",Published:"منتشرشده"};
export default function ContentStudioPage(){
 const [token,setToken]=useState("");const [orgs,setOrgs]=useState<Organization[]>([]);const [org,setOrg]=useState("");
 const [brands,setBrands]=useState<Brand[]>([]);const [items,setItems]=useState<Draft[]>([]);const [selected,setSelected]=useState("");
 const [detail,setDetail]=useState<Draft|null>(null);const [ai,setAi]=useState<AIStatus|null>(null);const [targets,setTargets]=useState<PublishTarget[]>([]);const [connectionId,setConnectionId]=useState("");const [targetChatId,setTargetChatId]=useState("");const [scheduledAt,setScheduledAt]=useState("");
 const [topic,setTopic]=useState("");const [brandId,setBrandId]=useState("");const [channel,setChannel]=useState("Instagram");const [tone,setTone]=useState("حرفه‌ای و متقاعدکننده");const [instructions,setInstructions]=useState("");
 const [title,setTitle]=useState("");const [content,setContent]=useState("");const [filter,setFilter]=useState("All");const [query,setQuery]=useState("");
 const [busy,setBusy]=useState(false);const [error,setError]=useState("");const [notice,setNotice]=useState("");
 const base="/api/organizations/"+org+"/content";
 async function refresh(access=token,organization=org){
  if(!access||!organization)return;
  const [drafts,aiStatus,brandList,publishTargets]=await Promise.all([
   api<Draft[]>("/api/organizations/"+organization+"/content/drafts?status="+filter+"&q="+encodeURIComponent(query),access),
   api<AIStatus>("/api/organizations/"+organization+"/content/ai/status",access),
   api<Brand[]>("/api/organizations/"+organization+"/brands",access),
   api<PublishTarget[]>("/api/organizations/"+organization+"/content/publish-targets",access)
  ]);
  setItems(drafts);setAi(aiStatus);setBrands(brandList);setTargets(publishTargets);if(!connectionId&&publishTargets.length)setConnectionId(publishTargets[0].id);
  if(selected&&!drafts.some(x=>x.id===selected)){setSelected("");setDetail(null);}
 }
 async function openDraft(id:string){
  setSelected(id);const result=await api<Draft>(base+"/drafts/"+id,token);
  setDetail(result);setTitle(result.title);setContent(result.content);
 }
 useEffect(()=>{
  const saved=localStorage.getItem("majazi_access_token")??"";setToken(saved);
  if(saved)api<Organization[]>("/api/organizations",saved).then(list=>{setOrgs(list);setOrg(list[0]?.id??"");}).catch(e=>setError(e instanceof Error?e.message:"دریافت سازمان‌ها ناموفق بود."));
 },[]);
 useEffect(()=>{if(token&&org)refresh(token,org).catch(e=>setError(e instanceof Error?e.message:"بارگذاری استودیو ناموفق بود."));},[token,org,filter]);
 const active=useMemo(()=>items.find(x=>x.id===selected),[items,selected]);
 async function run(action:()=>Promise<void>){setBusy(true);setError("");setNotice("");try{await action();}catch(e){setError(e instanceof Error?e.message:"عملیات ناموفق بود.");}finally{setBusy(false);}}
 async function generate(e:FormEvent<HTMLFormElement>){
  e.preventDefault();await run(async()=>{
   const created=await api<Draft>(base+"/generate",token,{method:"POST",body:JSON.stringify({brandId:brandId||null,topic,channel,tone,instructions})});
   setTopic("");await refresh();await openDraft(created.id);setNotice("محتوا با مدل هوش مصنوعی تولید و به‌عنوان نسخه اول ذخیره شد.");
  });
 }
 async function save(){
  await run(async()=>{
   await api(base+"/drafts/"+selected,token,{method:"PUT",body:JSON.stringify({title,content,changeNote:"ویرایش از استودیوی محتوا"})});
   await openDraft(selected);await refresh();setNotice("ویرایش ذخیره شد و نسخه جدیدی در تاریخچه ثبت شد.");
  });
 }
 async function schedule(){
  if(!selected)return;await run(async()=>{
   if(!connectionId||!targetChatId.trim()||!scheduledAt)throw new Error("اتصال تلگرام، شناسه مقصد و زمان انتشار را مشخص کنید.");
   await api(base+"/drafts/"+selected+"/schedule",token,{method:"POST",body:JSON.stringify({connectionId,targetChatId:targetChatId.trim(),scheduledAt:new Date(scheduledAt).toISOString()})});
   await openDraft(selected);await refresh();setNotice("محتوا برای انتشار زمان‌بندی شد.");
  });
 }
 async function publishNow(){
  if(!selected)return;await run(async()=>{
   if(!connectionId||!targetChatId.trim())throw new Error("اتصال تلگرام و شناسه مقصد را مشخص کنید.");
   await api(base+"/drafts/"+selected+"/publish-now",token,{method:"POST",body:JSON.stringify({connectionId,targetChatId:targetChatId.trim()})});
   await openDraft(selected);await refresh();setNotice("محتوا در صف ارسال فوری قرار گرفت.");
  });
 }
 async function cancelSchedule(){
  if(!selected)return;await run(async()=>{await api(base+"/drafts/"+selected+"/cancel-schedule",token,{method:"POST"});await openDraft(selected);await refresh();setNotice("زمان‌بندی لغو شد.");});
 }
 async function approve(){
  if(!selected)return;await run(async()=>{
   await api(base+"/drafts/"+selected+"/approve",token,{method:"POST"});
   await openDraft(selected);await refresh();setNotice("محتوا تأیید شد.");
  });
 }
 if(!token)return <main className="studio-shell"><section className="studio-empty"><h1>استودیوی محتوای MAJAZI</h1><p>برای دسترسی ابتدا وارد پنل شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;
 return <main className="studio-shell" dir="rtl">
  <header className="studio-header"><div><span className="studio-kicker">MAJAZI / AI CONTENT WORKSPACE</span><h1>استودیوی هوشمند محتوا</h1><p>تولید، ویرایش، نسخه‌بندی و تأیید محتوای برند در یک فضای کاری</p></div><Link href="/">بازگشت به داشبورد</Link></header>
  <section className="studio-toolbar"><label>فضای سازمانی<select value={org} onChange={e=>{setOrg(e.target.value);setSelected("");setDetail(null);}}>{orgs.map(x=><option key={x.id} value={x.id}>{x.name} · {x.role}</option>)}</select></label>
   <div className={"ai-connection "+(ai?.configured?"ready":"missing")}><i/>{ai?.configured?("هوش مصنوعی آماده · "+ai.model):"اتصال هوش مصنوعی تنظیم نشده"}</div>
   <button type="button" onClick={()=>void run(()=>refresh())} disabled={busy}>به‌روزرسانی</button>
  </section>
  {error&&<div className="studio-alert error" role="alert">{error}</div>}{notice&&<div className="studio-alert success" role="status">{notice}</div>}
  {!ai?.configured&&<section className="studio-config"><div><strong>اتصال مدل هوش مصنوعی</strong><p>برای تولید واقعی، متغیرهای امن AI__BaseUrl، AI__ApiKey و AI__Model را در محیط سرور تنظیم و API را راه‌اندازی مجدد کنید. کلید در پنل یا مرورگر نمایش داده نمی‌شود.</p></div><span>سازگار با OpenAI API</span></section>}
  <section className="studio-grid">
   <aside className="studio-generator">
    <div className="studio-section-title"><span>01 / CREATE</span><h2>تولید محتوای جدید</h2><p>موضوع و هدف را مشخص کنید؛ خروجی مستقیماً در بانک محتوا ذخیره می‌شود.</p></div>
    <form onSubmit={generate} className="studio-form">
     <label>موضوع یا ایده محتوا<textarea value={topic} onChange={e=>setTopic(e.target.value)} required maxLength={1500} rows={4} placeholder="مثلاً معرفی مزیت رقابتی محصول و دعوت مخاطب به اقدام..."/></label>
     <label>برند<select value={brandId} onChange={e=>setBrandId(e.target.value)}><option value="">بدون انتخاب برند</option>{brands.map(b=><option key={b.id} value={b.id}>{b.name}</option>)}</select></label>
     <div className="studio-form-row"><label>کانال انتشار<select value={channel} onChange={e=>setChannel(e.target.value)}>{["Instagram","Telegram","WhatsApp","Website","LinkedIn","Email","General"].map(x=><option key={x} value={x}>{x}</option>)}</select></label><label>لحن<select value={tone} onChange={e=>setTone(e.target.value)}>{["حرفه‌ای و متقاعدکننده","رسمی","دوستانه","لوکس و پریمیوم","آموزشی","خلاقانه","فروش‌محور"].map(x=><option key={x}>{x}</option>)}</select></label></div>
     <label>دستور تکمیلی<textarea value={instructions} onChange={e=>setInstructions(e.target.value)} maxLength={4000} rows={3} placeholder="مخاطب هدف، کلمات کلیدی، محدودیت‌ها و فراخوان اقدام..."/></label>
     <button className="studio-primary" disabled={busy||!ai?.configured||!org||!topic.trim()}>{busy?"در حال تولید و ذخیره…":"✦ تولید محتوا با هوش مصنوعی"}</button>
     <small>تولید فقط با سرویس واقعی انجام می‌شود؛ در صورت نبود اتصال، متن ساختگی ساخته نخواهد شد.</small>
    </form>
   </aside>
   <section className="studio-library">
    <div className="studio-section-title"><span>02 / LIBRARY</span><h2>کتابخانه محتوا</h2><p>پیش‌نویس‌ها و محتوای تأییدشده سازمان</p></div>
    <div className="studio-library-tools"><input value={query} onChange={e=>setQuery(e.target.value)} onKeyDown={e=>{if(e.key==="Enter")void run(()=>refresh());}} placeholder="جست‌وجوی عنوان یا متن..."/><select value={filter} onChange={e=>setFilter(e.target.value)}><option value="All">همه وضعیت‌ها</option><option value="Draft">پیش‌نویس</option><option value="Approved">تأییدشده</option></select><button onClick={()=>void run(()=>refresh())}>جست‌وجو</button></div>
    <div className="studio-draft-list">{items.length===0?<div className="studio-no-drafts"><strong>هنوز محتوایی ثبت نشده است</strong><p>اولین محتوا را از فرم تولید بسازید یا پس از تولید در این فهرست ویرایش کنید.</p></div>:items.map(item=><button key={item.id} className={"studio-draft-row "+(selected===item.id?"active":"")} onClick={()=>void run(()=>openDraft(item.id))}><span className="draft-channel">{item.channel}</span><span className="draft-main"><strong>{item.title}</strong><small>{new Date(item.updatedAt).toLocaleString("fa-IR")} · {item.versionCount??0} نسخه</small></span><span className={"draft-status "+(item.status==="Approved"?"approved":"")}>{statuses[item.status]??item.status}</span></button>)}</div>
   </section>
  </section>
  {detail&&<section className="studio-editor">
   <div className="studio-editor-heading"><div><span>03 / EDITOR · {detail.aiModel||"MANUAL"}</span><h2>ویرایش و بازبینی محتوا</h2><p>هر ذخیره‌سازی یک نسخه مستقل در تاریخچه ایجاد می‌کند.</p></div><span className={"draft-status "+(detail.status==="Approved"||detail.status==="Published"?"approved":"")}>{statuses[detail.status]??detail.status}</span></div>
   <div className="studio-editor-grid"><div className="studio-edit-fields"><label>عنوان محتوا<input value={title} onChange={e=>setTitle(e.target.value)} maxLength={240}/></label><label>متن محتوا<textarea value={content} onChange={e=>setContent(e.target.value)} rows={15} maxLength={30000}/></label><div className="studio-editor-actions"><button className="studio-primary" disabled={busy||!title.trim()||!content.trim()} onClick={()=>void save()}>ذخیره و ایجاد نسخه</button><button className="studio-approve" disabled={busy||detail.status==="Approved"||!["Owner","Admin"].includes(orgs.find(x=>x.id===org)?.role??"")} onClick={()=>void approve()}>تأیید نهایی</button></div><small>تأیید نهایی فقط برای مالک یا مدیر سازمان فعال است. با ویرایش محتوای تأییدشده، وضعیت آن دوباره پیش‌نویس می‌شود.</small>
    {detail.channel==="Telegram"&&<section className="studio-publish"><h3>انتشار تلگرام</h3><p>انتشار فقط پس از تأیید نهایی و انتخاب مقصد مشخص انجام می‌شود.</p>
     <label>ربات متصل<select value={connectionId} onChange={e=>setConnectionId(e.target.value)}><option value="">انتخاب اتصال</option>{targets.map(t=><option key={t.id} value={t.id}>{t.displayName}</option>)}</select></label>
     <label>شناسه عددی چت / کانال مقصد<input value={targetChatId} onChange={e=>setTargetChatId(e.target.value)} placeholder="مثلاً -1001234567890"/></label>
     <div className="studio-schedule-row"><label>زمان انتشار<input type="datetime-local" value={scheduledAt} onChange={e=>setScheduledAt(e.target.value)}/></label><button className="studio-approve" disabled={busy||detail.status!=="Approved"||!targets.length||!targetChatId.trim()||!scheduledAt} onClick={()=>void schedule()}>زمان‌بندی انتشار</button></div>
     <div className="studio-editor-actions"><button className="studio-primary" disabled={busy||detail.status!=="Approved"||!targets.length||!targetChatId.trim()} onClick={()=>void publishNow()}>ارسال فوری به تلگرام</button>{detail.status==="Scheduled"&&<button className="studio-approve" disabled={busy} onClick={()=>void cancelSchedule()}>لغو زمان‌بندی</button>}</div>
     {detail.status==="Scheduled"&&<small>زمان انتشار: {detail.scheduledAt?new Date(detail.scheduledAt).toLocaleString("fa-IR"):"در صف"}</small>}
     {detail.status==="PublishFailed"&&<small className="publish-error">ارسال ناموفق بود؛ اتصال ربات، مجوز ارسال و شناسه مقصد را بررسی کنید. سپس زمان‌بندی را دوباره انجام دهید.</small>}
     {detail.status==="Published"&&<small>منتشرشده در {detail.publishedAt?new Date(detail.publishedAt).toLocaleString("fa-IR"):""}</small>}
    </section>}</div>
    <aside className="studio-history"><h3>تاریخچه نسخه‌ها</h3>{(detail.versions??[]).map(v=><button key={v.id} onClick={()=>{setTitle(v.title);setContent(v.content);}}><b>نسخه {v.versionNumber}</b><span>{v.changeNote}</span><small>{new Date(v.createdAt).toLocaleString("fa-IR")}</small></button>)}</aside>
   </div>
  </section>}
  <footer className="studio-footer">MAJAZI · محتوای شما، با کنترل کامل تیم شما</footer>
 </main>;
}
