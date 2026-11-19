"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import "./content.css";

type Organization={id:string;name:string;role:string};
type Brand={id:string;name:string};
type Draft={id:string;scheduledAt?:string|null;publishedAt?:string|null;publishError?:string|null;brandId?:string|null;title:string;content:string;channel:string;tone:string;status:string;aiModel?:string|null;createdAt:string;updatedAt:string;approvedAt?:string|null;versionCount?:number;versions?:Version[]};
type Version={id:string;versionNumber:number;title:string;content:string;changeNote:string;createdAt:string};
type Approval={id:string;reviewRound:number;stageNumber:number;decision:string;comment?:string|null;actorUserId:string;createdAt:string};
type AIStatus={configured:boolean;model:string;provider:string;keyStored:boolean};
type PublishTarget={id:string;displayName:string;externalAccountId?:string|null};
type CalendarItem={id:string;title:string;channel:string;status:string;scheduledAt?:string|null;publishedAt?:string|null;publishError?:string|null};
async function api<T>(path:string,token:string,init?:RequestInit):Promise<T>{
 const response=await fetch("/backend-api"+path,{...init,headers:{"Content-Type":"application/json",Authorization:"Bearer "+token,...init?.headers},cache:"no-store"});
 const data=await response.json().catch(()=>null);
 if(!response.ok)throw new Error(data?.error??data?.detail??"درخواست ناموفق بود.");
 return data as T;
}
const statuses:Record<string,string>={Draft:"پیش‌نویس",InReview:"در انتظار تأیید دوم",Approved:"تأییدشده",Scheduled:"زمان‌بندی‌شده",Publishing:"در حال انتشار",Published:"منتشرشده",PublishFailed:"خطای انتشار"};
export default function ContentStudioPage(){
 const [token,setToken]=useState("");const [orgs,setOrgs]=useState<Organization[]>([]);const [org,setOrg]=useState("");
 const [brands,setBrands]=useState<Brand[]>([]);const [items,setItems]=useState<Draft[]>([]);const [calendarItems,setCalendarItems]=useState<CalendarItem[]>([]);const [selected,setSelected]=useState("");
 const [detail,setDetail]=useState<Draft|null>(null);const [approvals,setApprovals]=useState<Approval[]>([]);const [approvalComment,setApprovalComment]=useState("");const [ai,setAi]=useState<AIStatus|null>(null);const [targets,setTargets]=useState<PublishTarget[]>([]);const [connectionId,setConnectionId]=useState("");const [targetChatId,setTargetChatId]=useState("");const [scheduledAt,setScheduledAt]=useState("");
 const [topic,setTopic]=useState("");const [manualTitle,setManualTitle]=useState("");const [manualContent,setManualContent]=useState("");const [createMode,setCreateMode]=useState<"ai"|"manual">("ai");const [brandId,setBrandId]=useState("");const [channel,setChannel]=useState("Instagram");const [tone,setTone]=useState("حرفه‌ای و متقاعدکننده");const [instructions,setInstructions]=useState("");
 const [title,setTitle]=useState("");const [content,setContent]=useState("");const [filter,setFilter]=useState("All");const [query,setQuery]=useState("");
 const [busy,setBusy]=useState(false);const [error,setError]=useState("");const [notice,setNotice]=useState("");
 const base="/api/organizations/"+org+"/content";
 async function refresh(access=token,organization=org){
  if(!access||!organization)return;
  const [drafts,aiStatus,brandList,publishTargets,calendar]=await Promise.all([
   api<Draft[]>("/api/organizations/"+organization+"/content/drafts?status="+filter+"&q="+encodeURIComponent(query),access),
   api<AIStatus>("/api/organizations/"+organization+"/content/ai/status",access),
   api<Brand[]>("/api/organizations/"+organization+"/brands",access),
   api<PublishTarget[]>("/api/organizations/"+organization+"/content/publish-targets",access),
   api<{items:CalendarItem[]}>("/api/organizations/"+organization+"/content/calendar",access)
  ]);
  setItems(drafts);setAi(aiStatus);setBrands(brandList);setTargets(publishTargets);setCalendarItems(calendar.items);if(!connectionId&&publishTargets.length)setConnectionId(publishTargets[0].id);
  if(selected&&!drafts.some(x=>x.id===selected)){setSelected("");setDetail(null);}
 }
 async function openDraft(id:string){
  setSelected(id);
  const [result,history]=await Promise.all([
   api<Draft>(base+"/drafts/"+id,token),
   api<Approval[]>(base+"/drafts/"+id+"/approvals",token)
  ]);
  setDetail(result);setApprovals(history);setApprovalComment("");setTitle(result.title);setContent(result.content);
 }
 useEffect(()=>{
  const requestedMode=new URLSearchParams(window.location.search).get("mode");if(requestedMode==="manual")setCreateMode("manual");else if(requestedMode==="ai")setCreateMode("ai");const saved=localStorage.getItem("majazi_access_token")??"";setToken(saved);
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
 async function createManual(e:FormEvent<HTMLFormElement>){
  e.preventDefault();await run(async()=>{
   const created=await api<Draft>(base+"/drafts",token,{method:"POST",body:JSON.stringify({brandId:brandId||null,title:manualTitle,content:manualContent,channel,tone})});
   setManualTitle("");setManualContent("");await refresh();await openDraft(created.id);setNotice("پیش‌نویس دستی ذخیره شد؛ اکنون می‌توانید آن را ویرایش و برای تأیید ارسال کنید.");
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
   const result=await api<{status:string;message:string}>(base+"/drafts/"+selected+"/approve",token,{method:"POST",body:JSON.stringify({comment:approvalComment})});
   await openDraft(selected);await refresh();setNotice(result.message);
  });
 }
 async function reject(){
  if(!selected)return;await run(async()=>{
   if(!approvalComment.trim())throw new Error("برای رد محتوا، دلیل بازبینی را وارد کنید.");
   await api(base+"/drafts/"+selected+"/reject",token,{method:"POST",body:JSON.stringify({comment:approvalComment})});
   await openDraft(selected);await refresh();setNotice("محتوا برای اصلاح به پیش‌نویس بازگشت.");
  });
 }
 if(!token)return <main className="studio-shell"><section className="studio-empty"><h1>استودیوی محتوای MAJAZI</h1><p>برای دسترسی ابتدا وارد پنل شوید.</p><Link href="/">بازگشت به داشبورد</Link></section></main>;
 return <main className="studio-shell" dir="rtl">
  <header className="studio-header"><div><span className="studio-kicker">MAJAZI / AI CONTENT WORKSPACE</span><h1>استودیوی هوشمند محتوا</h1><p>تولید، ویرایش، نسخه‌بندی و تأیید محتوای برند در یک فضای کاری</p></div><Link href="/">بازگشت به داشبورد</Link></header>
  <section className="studio-toolbar"><label>فضای سازمانی<select value={org} onChange={e=>{setOrg(e.target.value);setSelected("");setDetail(null);setApprovals([]);setConnectionId("");setTargetChatId("");setScheduledAt("");}}>{orgs.map(x=><option key={x.id} value={x.id}>{x.name} · {x.role}</option>)}</select></label>
   <div className={"ai-connection "+(ai?.configured?"ready":"missing")}><i/>{ai?.configured?("هوش مصنوعی آماده · "+ai.model):"اتصال هوش مصنوعی تنظیم نشده"}</div>
   <button type="button" onClick={()=>void run(()=>refresh())} disabled={busy}>به‌روزرسانی</button>
  </section>
  {error&&<div className="studio-alert error" role="alert">{error}</div>}{notice&&<div className="studio-alert success" role="status">{notice}</div>}
  {!ai?.configured&&<section className="studio-config"><div><strong>اتصال مدل هوش مصنوعی</strong><p>برای تولید واقعی، متغیرهای امن AI__BaseUrl، AI__ApiKey و AI__Model را در محیط سرور تنظیم و API را راه‌اندازی مجدد کنید. کلید در پنل یا مرورگر نمایش داده نمی‌شود.</p></div><span>سازگار با OpenAI API</span></section>}
  <section className="studio-library studio-calendar">
   <div className="studio-section-title"><span>CONTENT OPERATIONS / CALENDAR</span><h2>تقویم انتشار</h2><p>برنامه انتشار ۳۰ روز گذشته تا ۹۰ روز آینده و سوابق انتشار</p></div>
   <div className="studio-draft-list">{calendarItems.length===0?<div className="studio-no-drafts"><strong>رویدادی در این بازه وجود ندارد</strong><p>پس از تأیید محتوا و زمان‌بندی انتشار، رویدادها اینجا نمایش داده می‌شوند.</p></div>:calendarItems.map(item=><button key={item.id} className="studio-draft-row" onClick={()=>void run(()=>openDraft(item.id))}><span className="draft-channel">{item.channel}</span><span className="draft-main"><strong>{item.title}</strong><small>{item.publishedAt?"انتشار واقعی: "+new Date(item.publishedAt).toLocaleString("fa-IR"):item.scheduledAt?"زمان برنامه‌ریزی‌شده: "+new Date(item.scheduledAt).toLocaleString("fa-IR"):"بدون زمان ثبت‌شده"}</small></span><span className={"draft-status "+(item.status==="Published"?"approved":"")}>{statuses[item.status]??item.status}</span></button>)}</div>
  </section>
  <section className="studio-grid">
   <aside className="studio-generator">
    <div className="studio-section-title"><span>01 / CREATE</span><h2>ایجاد محتوا</h2><p>برای هر پیام‌رسان، محتوای اختصاصی بسازید؛ با هوش مصنوعی یا به‌صورت دستی.</p></div>
    <div className="studio-create-modes"><button type="button" className={createMode==="ai"?"active":""} onClick={()=>setCreateMode("ai")}>✦ ایجاد با هوش مصنوعی</button><button type="button" className={createMode==="manual"?"active":""} onClick={()=>setCreateMode("manual")}>✎ ایجاد دستی</button></div>
    <div className="studio-platform-picker" aria-label="انتخاب پیام‌رسان و شبکه اجتماعی">{[{id:"Instagram",name:"اینستاگرام",icon:"◎"},{id:"Telegram",name:"تلگرام",icon:"➤"},{id:"WhatsApp",name:"واتساپ",icon:"◉"},{id:"Facebook",name:"فیسبوک",icon:"f"},{id:"YouTube",name:"یوتیوب",icon:"▶"},{id:"TikTok",name:"تیک‌تاک",icon:"♪"},{id:"LinkedIn",name:"لینکدین",icon:"in"},{id:"X",name:"ایکس",icon:"𝕏"},{id:"Bale",name:"بله",icon:"ب"},{id:"Rubika",name:"روبیکا",icon:"ر"},{id:"Eitaa",name:"ایتا",icon:"ا"},{id:"Aparat",name:"آپارات",icon:"آپ"},{id:"SoroushPlus",name:"سروش‌پلاس",icon:"س"},{id:"Gap",name:"گپ",icon:"گ"},{id:"iGap",name:"آی‌گپ",icon:"آ"},{id:"Shad",name:"شاد",icon:"ش"},{id:"Discord",name:"دیسکورد",icon:"◈"},{id:"Pinterest",name:"پینترست",icon:"P"},{id:"Threads",name:"تردز",icon:"@"},{id:"Snapchat",name:"اسنپ‌چت",icon:"👻"},{id:"Viber",name:"وایبر",icon:"V"},{id:"LINE",name:"لاین",icon:"L"},{id:"WeChat",name:"وی‌چت",icon:"W"},{id:"Reddit",name:"ردیت",icon:"●"},{id:"Mastodon",name:"ماستودون",icon:"m"},{id:"Bluesky",name:"بلواسکای",icon:"🦋"},{id:"Twitch",name:"توییچ",icon:"▣"},{id:"Messenger",name:"مسنجر",icon:"ϟ"}].map(x=><button type="button" key={x.id} className={channel===x.id?"selected":""} onClick={()=>setChannel(x.id)}><i>{x.icon}</i><span>{x.name}</span></button>)}</div>
    {createMode==="ai"?<form onSubmit={generate} className="studio-form">
     <label>موضوع یا ایده محتوا<textarea value={topic} onChange={e=>setTopic(e.target.value)} required maxLength={1500} rows={4} placeholder="مثلاً معرفی مزیت رقابتی محصول و دعوت مخاطب به اقدام..."/></label>
     <label>برند<select value={brandId} onChange={e=>setBrandId(e.target.value)}><option value="">بدون انتخاب برند</option>{brands.map(b=><option key={b.id} value={b.id}>{b.name}</option>)}</select></label>
     <div className="studio-form-row"><label>کانال انتشار<select value={channel} onChange={e=>setChannel(e.target.value)}>{["Instagram","Telegram","WhatsApp","Website","LinkedIn","Email","General"].map(x=><option key={x} value={x}>{x}</option>)}</select></label><label>لحن<select value={tone} onChange={e=>setTone(e.target.value)}>{["حرفه‌ای و متقاعدکننده","رسمی","دوستانه","لوکس و پریمیوم","آموزشی","خلاقانه","فروش‌محور"].map(x=><option key={x}>{x}</option>)}</select></label></div>
     <label>دستور تکمیلی<textarea value={instructions} onChange={e=>setInstructions(e.target.value)} maxLength={4000} rows={3} placeholder="مخاطب هدف، کلمات کلیدی، محدودیت‌ها و فراخوان اقدام..."/></label>
     <button className="studio-primary" disabled={busy||!ai?.configured||!org||!topic.trim()}>{busy?"در حال تولید و ذخیره…":"✦ تولید محتوا با هوش مصنوعی"}</button>
     <small>تولید فقط با سرویس واقعی انجام می‌شود؛ در صورت نبود اتصال، متن ساختگی ساخته نخواهد شد.</small>
    </form>:<form onSubmit={createManual} className="studio-form">
     <label>عنوان محتوا<input required maxLength={240} value={manualTitle} onChange={e=>setManualTitle(e.target.value)} placeholder="عنوان مخصوص این پیام‌رسان"/></label>
     <label>متن محتوا<textarea required maxLength={30000} rows={9} value={manualContent} onChange={e=>setManualContent(e.target.value)} placeholder="متن، هشتگ‌ها، لینک و فراخوان اقدام..."/></label>
     <label>برند<select value={brandId} onChange={e=>setBrandId(e.target.value)}><option value="">بدون انتخاب برند</option>{brands.map(b=><option key={b.id} value={b.id}>{b.name}</option>)}</select></label>
     <button className="studio-primary" disabled={busy||!org||!manualTitle.trim()||!manualContent.trim()}>ذخیره پیش‌نویس دستی</button>
     <small>پیش‌نویس دستی هم وارد گردش نسخه‌بندی و تأیید محتوا می‌شود.</small>
    </form>}
   </aside>
   <section className="studio-library">
    <div className="studio-section-title"><span>02 / LIBRARY</span><h2>کتابخانه محتوا</h2><p>پیش‌نویس‌ها و محتوای تأییدشده سازمان</p></div>
    <div className="studio-library-tools"><input value={query} onChange={e=>setQuery(e.target.value)} onKeyDown={e=>{if(e.key==="Enter")void run(()=>refresh());}} placeholder="جست‌وجوی عنوان یا متن..."/><select value={filter} onChange={e=>setFilter(e.target.value)}><option value="All">همه وضعیت‌ها</option><option value="Draft">پیش‌نویس</option><option value="InReview">در انتظار تأیید دوم</option><option value="Approved">تأییدشده</option><option value="Scheduled">زمان‌بندی‌شده</option><option value="Publishing">در حال انتشار</option><option value="Published">منتشرشده</option><option value="PublishFailed">خطای انتشار</option></select><button onClick={()=>void run(()=>refresh())}>جست‌وجو</button></div>
    <div className="studio-draft-list">{items.length===0?<div className="studio-no-drafts"><strong>هنوز محتوایی ثبت نشده است</strong><p>اولین محتوا را از فرم تولید بسازید یا پس از تولید در این فهرست ویرایش کنید.</p></div>:items.map(item=><button key={item.id} className={"studio-draft-row "+(selected===item.id?"active":"")} onClick={()=>void run(()=>openDraft(item.id))}><span className="draft-channel">{item.channel}</span><span className="draft-main"><strong>{item.title}</strong><small>{item.scheduledAt?"انتشار: "+new Date(item.scheduledAt).toLocaleString("fa-IR"):new Date(item.updatedAt).toLocaleString("fa-IR")} · {item.versionCount??0} نسخه</small></span><span className={"draft-status "+(item.status==="Approved"?"approved":"")}>{statuses[item.status]??item.status}</span></button>)}</div>
   </section>
  </section>
  {detail&&<section className="studio-editor">
   <div className="studio-editor-heading"><div><span>03 / EDITOR · {detail.aiModel||"MANUAL"}</span><h2>ویرایش و بازبینی محتوا</h2><p>هر ذخیره‌سازی یک نسخه مستقل در تاریخچه ایجاد می‌کند.</p></div><span className={"draft-status "+(detail.status==="Approved"||detail.status==="Published"?"approved":"")}>{statuses[detail.status]??detail.status}</span></div>
   <div className="studio-editor-grid"><div className="studio-edit-fields"><label>عنوان محتوا<input value={title} onChange={e=>setTitle(e.target.value)} maxLength={240}/></label><label>متن محتوا<textarea value={content} onChange={e=>setContent(e.target.value)} rows={15} maxLength={30000}/></label><div className="studio-editor-actions"><button className="studio-primary" disabled={busy||!title.trim()||!content.trim()} onClick={()=>void save()}>ذخیره و ایجاد نسخه</button><button className="studio-approve" disabled={busy||!["Draft","InReview"].includes(detail.status)||!["Owner","Admin"].includes(orgs.find(x=>x.id===org)?.role??"")} onClick={()=>void approve()}>{detail.status==="InReview"?"ثبت تأیید مرحله دوم":"ثبت تأیید مرحله اول"}</button><button className="studio-approve" disabled={busy||!["Draft","InReview"].includes(detail.status)||!approvalComment.trim()||!["Owner","Admin"].includes(orgs.find(x=>x.id===org)?.role??"")} onClick={()=>void reject()}>رد و بازگشت برای اصلاح</button></div><label>یادداشت بازبینی (برای رد الزامی)<textarea value={approvalComment} onChange={e=>setApprovalComment(e.target.value)} rows={2} maxLength={1000} placeholder="نکات اصلاح یا توضیح تأیید..."/></label><div className="studio-history"><h3>گردش تأیید دو مرحله‌ای</h3>{approvals.length===0?<small>هنوز تصمیمی ثبت نشده است.</small>:approvals.map(a=><div key={a.id}><b>دور {a.reviewRound} · مرحله {a.stageNumber} · {a.decision==="Approved"?"تأیید":"رد"}</b><span>{a.comment||"بدون یادداشت"}</span><small>{new Date(a.createdAt).toLocaleString("fa-IR")}</small></div>)}</div><small>تأیید نهایی نیازمند دو مدیر متفاوت (مالک یا مدیر سازمان) است. ویرایش محتوا در جریان بازبینی، دور جدیدی ایجاد می‌کند.</small>
    {<section className="studio-publish"><h3>ارسال به {detail.channel==="Instagram"?"اینستاگرام":detail.channel==="Telegram"?"تلگرام":detail.channel==="Bale"?"بله":detail.channel==="Rubika"?"روبیکا":detail.channel==="Eitaa"?"ایتا":detail.channel==="WhatsApp"?"واتساپ":detail.channel}</h3><p>انتشار فقط پس از تأیید نهایی و انتخاب مقصد مشخص انجام می‌شود.</p>
     {detail.channel==="Telegram"&&<><label>ربات متصل<select value={connectionId} onChange={e=>setConnectionId(e.target.value)}><option value="">انتخاب اتصال</option>{targets.map(t=><option key={t.id} value={t.id}>{t.displayName}</option>)}</select></label>
     <label>شناسه عددی چت / کانال مقصد<input value={targetChatId} onChange={e=>setTargetChatId(e.target.value)} placeholder="مثلاً -1001234567890 یا @channelname"/></label>
     <div className="studio-schedule-row"><label>زمان انتشار<input type="datetime-local" value={scheduledAt} onChange={e=>setScheduledAt(e.target.value)}/></label><button className="studio-approve" disabled={busy||detail.status!=="Approved"||!targets.length||!targetChatId.trim()||!scheduledAt} onClick={()=>void schedule()}>زمان‌بندی انتشار</button></div>
     <div className="studio-editor-actions"><button className="studio-primary" disabled={busy||detail.status!=="Approved"||!targets.length||!targetChatId.trim()} onClick={()=>void publishNow()}>ارسال فوری به تلگرام</button>{detail.status==="Scheduled"&&<button className="studio-approve" disabled={busy} onClick={()=>void cancelSchedule()}>لغو زمان‌بندی</button>}</div>
     {detail.status==="Scheduled"&&<small>زمان انتشار: {detail.scheduledAt?new Date(detail.scheduledAt).toLocaleString("fa-IR"):"در صف"}</small>}
     {detail.status==="PublishFailed"&&<small className="publish-error">ارسال ناموفق بود: {detail.publishError||"اتصال ربات، مجوز ارسال و شناسه مقصد را بررسی کنید."} سپس محتوا را دوباره تأیید و زمان‌بندی کنید.</small>}
     {detail.status==="Published"&&<small>منتشرشده در {detail.publishedAt?new Date(detail.publishedAt).toLocaleString("fa-IR"):""}</small>}
    </>}{detail.channel!=="Telegram"&&<small>آداپتور ارسال رسمی {detail.channel} هنوز در MAJAZI فعال نشده است. محتوا ذخیره و قابل ویرایش و تأیید است؛ ارسال واقعی پس از پیاده‌سازی و اتصال API رسمی همین پلتفرم فعال می‌شود.</small>}
    </section>}</div>
    <aside className="studio-history"><h3>تاریخچه نسخه‌ها</h3>{(detail.versions??[]).map(v=><button key={v.id} onClick={()=>{setTitle(v.title);setContent(v.content);}}><b>نسخه {v.versionNumber}</b><span>{v.changeNote}</span><small>{new Date(v.createdAt).toLocaleString("fa-IR")}</small></button>)}</aside>
   </div>
  </section>}
  <footer className="studio-footer">MAJAZI · محتوای شما، با کنترل کامل تیم شما</footer>
 </main>;
}
