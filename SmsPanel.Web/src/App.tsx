import { useEffect, useRef, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { isValidJalaaliDate, toGregorian, toJalaali } from "jalaali-js";
import "./App.css";

type Page =
  | "dashboard"
  | "imports"
  | "merchants"
  | "send"
  | "campaigns"
  | "jobs"
  | "inbox"
  | "issues"
  | "users"
  | "settings";
type Dashboard = {
  peopleCount: number;
  activeCampaignCount: number;
  sentCount: number;
  receivedIbanCount: number;
  openIssueCount: number;
};
type Issue = {
  id: number;
  senderMobile: string;
  extractedIban?: string;
  issueType: string;
};
type TemplateField = {
  name: string;
  token: string;
  label: string;
  sample: string;
  sensitive: boolean;
};
type ImportBatch = {
  id: number;
  originalFileName: string;
  status: string;
  totalRows: number;
  processedRows: number;
  insertedRows: number;
  duplicateRows: number;
  invalidRows: number;
  currentStage?: string;
  lastError?: string;
  createdAtUtc: string;
};
type CampaignJob = {
  id: number;
  name: string;
  status: string;
  startsAtUtc: string;
  total: number;
  pending: number;
  processing: number;
  sent: number;
  failed: number;
};
type FailedOutboundMessage = { id: number; fullName: string; mobile: string; attemptCount: number; error: string };
type CampaignSummary = {
  id: number;
  name: string;
  messageTemplate: string;
  confirmationEnabled: boolean;
  confirmationTemplate?: string;
  status: "Draft" | "Active" | "Closed";
  startsAtUtc: string;
  endsAtUtc?: string;
  recipientCount: number;
  sentCount: number;
  failedCount: number;
};
type CampaignSeed = Pick<CampaignSummary, "name" | "messageTemplate" | "confirmationEnabled" | "confirmationTemplate">;
type Inbound = {
  id: number;
  providerMessageId: string;
  senderMobile: string;
  rawText: string;
  receivedAtUtc: string;
  processingStatus: string;
  lastError?: string;
  iban?: string;
  issueType?: string;
};
type CurrentUser = { username: string; displayName: string; isAdmin: boolean };
type ManagedUser = {
  id: number;
  username: string;
  displayName: string;
  isAdmin: boolean;
  isActive: boolean;
  createdAtUtc: string;
  lastLoginAtUtc?: string;
};
type Merchant = {
  id: number;
  terminalNumber: string;
  nationalId: string;
  merchantName: string;
  mobile?: string;
  status?: string;
  currentIban?: string;
  bank?: string;
  agency?: string;
  personId?: number;
  hasMobile: boolean;
};
type Recipient = {
  id: number;
  firstName: string;
  lastName: string;
  nationalCode: string;
  mobile: string;
};
const emptyDashboard: Dashboard = {
  peopleCount: 0,
  activeCampaignCount: 0,
  sentCount: 0,
  receivedIbanCount: 0,
  openIssueCount: 0,
};
const issueLabels: Record<string, string> = {
  MobileNotFound: "شماره ناشناس",
  MobileMatchesMultiplePeople: "موبایل تکراری",
  NoIbanFound: "شبا پیدا نشد",
  InvalidIban: "شبای نامعتبر",
  MultipleIbansFound: "چند شبا",
  IbanEqualsCurrentIban: "برابر حساب فعلی",
  IbanUsedByAnotherPerson: "شبای تکراری",
  MultipleDifferentReplies: "پاسخ متفاوت",
  RequestNotActive: "درخواست غیرفعال",
  RequestAmbiguous: "کمپین مبهم",
  IncompleteIban: "شماره شبا ناقص است",
};
const fa = (value: number) => value.toLocaleString("fa-IR");
const latinDigits = (value: string) =>
  value
    .replace(/[۰-۹]/g, (d) => String("۰۱۲۳۴۵۶۷۸۹".indexOf(d)))
    .replace(/[٠-٩]/g, (d) => String("٠١٢٣٤٥٦٧٨٩".indexOf(d)));
const jalaliDeadline = (date: string, time: string) => {
  const parts = latinDigits(date).trim().split(/[\/-]/).map(Number);
  if (parts.length !== 3 || !isValidJalaaliDate(parts[0], parts[1], parts[2]))
    throw new Error("تاریخ شمسی معتبر نیست. نمونه صحیح: ۱۴۰۵/۰۸/۲۰");
  const clock = latinDigits(time).split(":").map(Number);
  if (
    clock.length !== 2 ||
    clock[0] < 0 ||
    clock[0] > 23 ||
    clock[1] < 0 ||
    clock[1] > 59
  )
    throw new Error("ساعت مهلت معتبر نیست.");
  const g = toGregorian(parts[0], parts[1], parts[2]);
  return new Date(g.gy, g.gm - 1, g.gd, clock[0], clock[1]).toISOString();
};
const jalaliParts = (value?: string) => {
  if (!value) return { date: "", time: "" };
  const source = new Date(value);
  const j = toJalaali(source.getFullYear(), source.getMonth() + 1, source.getDate());
  return {
    date: `${j.jy}/${String(j.jm).padStart(2, "0")}/${String(j.jd).padStart(2, "0")}`,
    time: `${String(source.getHours()).padStart(2, "0")}:${String(source.getMinutes()).padStart(2, "0")}`,
  };
};

function App() {
  const [page, setPage] = useState<Page>("dashboard");
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const [stats, setStats] = useState(emptyDashboard);
  const [issues, setIssues] = useState<Issue[]>([]);
  const [online, setOnline] = useState(false);
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [needsSetup, setNeedsSetup] = useState(false);
  const [authReady, setAuthReady] = useState(false);
  const [campaignSeed, setCampaignSeed] = useState<CampaignSeed | null>(null);
  const refresh = () =>
    Promise.all([
      fetch("/health"),
      fetch("/api/dashboard"),
      fetch("/api/reconciliation-issues"),
    ])
      .then(async ([h, d, i]) => {
        setOnline(h.ok);
        if (d.ok) setStats(await d.json());
        if (i.ok) setIssues(await i.json());
      })
      .catch(() => setOnline(false));
  useEffect(() => {
    fetch("/api/auth/status")
      .then((r) => r.json())
      .then((x) => {
        setUser(x.user);
        setNeedsSetup(x.needsSetup);
        setAuthReady(true);
        if (x.authenticated) refresh();
      })
      .catch(() => setAuthReady(true));
  }, []);
  if (!authReady)
    return <div className="auth-loading">در حال آماده‌سازی سامانه…</div>;
  if (!user)
    return (
      <AuthPage
        needsSetup={needsSetup}
        onSuccess={(u) => {
          setUser(u);
          setNeedsSetup(false);
          refresh();
        }}
      />
    );
  const logout = async () => {
    await fetch("/api/auth/logout", { method: "POST" });
    setUser(null);
  };
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <Brand />
        <p className="side-caption">مدیریت سامانه</p>
        <Nav page={page} setPage={setPage} isAdmin={user.isAdmin} />
        <div className="sidebar-help">
          <b>{user.displayName}</b>
          <button onClick={logout}>خروج از سامانه</button>
          <span className={online ? "online" : "offline"}>
            {online ? "سرویس در دسترس است" : "اتصال برقرار نیست"}
          </span>
        </div>
      </aside>
      <section className="main-panel">
        <header className="app-header">
          <button
            className="mobile-menu-button"
            onClick={() => setMobileMenuOpen(true)}
            aria-label="باز کردن منوی اصلی"
          >
            ☰
          </button>
          <div className="header-title">
            <b>
              {
                {
                  dashboard: "داشبورد",
                  imports: "ورود اطلاعات",
                  merchants: "پذیرندگان",
                  send: "ارسال پیامک",
                  campaigns: "مدیریت کمپین‌ها",
                  jobs: "روند ارسال",
                  inbox: "پیام‌های دریافتی",
                  issues: "مغایرت‌ها",
                  users: "مدیریت کاربران",
                  settings: "تنظیمات",
                }[page]
              }
            </b>
            <span>سامانه جمع‌آوری شبای پشتیبان</span>
          </div>
          <span className="avatar">{user.displayName.slice(0, 1)}</span>
        </header>
        <div className="main-inner">
          {page === "dashboard" && (
            <DashboardPage stats={stats} goSend={() => setPage("send")} />
          )}{" "}
          {page === "imports" && <ImportsPage />}{" "}
          {page === "merchants" && <MerchantsPage />}{" "}
          {page === "send" && (
            <SendPage
              peopleCount={stats.peopleCount}
              seed={campaignSeed}
              onCreated={() => {
                setCampaignSeed(null);
                refresh();
                setPage("campaigns");
              }}
            />
          )}{" "}
          {page === "campaigns" && <CampaignsPage onReuse={(campaign) => { setCampaignSeed(campaign); setPage("send"); }} />}{" "}
          {page === "jobs" && <JobsPage />} {page === "inbox" && <InboxPage />}{" "}
          {page === "issues" && <IssuesPage issues={issues} />}{" "}
          {page === "users" && user.isAdmin && <UsersPage />}{" "}
          {page === "settings" && <SettingsPage />}
        </div>
      </section>
      {mobileMenuOpen && (
        <>
          <button
            className="mobile-menu-backdrop"
            onClick={() => setMobileMenuOpen(false)}
            aria-label="بستن منو"
          />
          <aside className="mobile-menu" aria-label="منوی اصلی موبایل">
            <div className="mobile-menu-head">
              <Brand />
              <button onClick={() => setMobileMenuOpen(false)} aria-label="بستن منو">×</button>
            </div>
            <p className="side-caption">مدیریت سامانه</p>
            <Nav
              page={page}
              setPage={(nextPage) => {
                setPage(nextPage);
                setMobileMenuOpen(false);
              }}
              isAdmin={user.isAdmin}
            />
            <div className="sidebar-help">
              <b>{user.displayName}</b>
              <button onClick={logout}>خروج از سامانه</button>
              <span className={online ? "online" : "offline"}>
                {online ? "سرویس در دسترس است" : "اتصال برقرار نیست"}
              </span>
            </div>
          </aside>
        </>
      )}
    </div>
  );
}

function Brand({ compact = false }: { compact?: boolean }) {
  return (
    <div className={`brand ${compact ? "compact" : ""}`}>
      <span className="brand-mark">پ</span>
      <span>
        <b>پنل پیامک</b>
        <small>مدیریت دریافت شماره شبا</small>
      </span>
    </div>
  );
}
function Nav({
  page,
  setPage,
  isAdmin,
}: {
  page: Page;
  setPage: (p: Page) => void;
  isAdmin: boolean;
}) {
  const [openGroup, setOpenGroup] = useState<"merchants" | "sms" | "system" | null>(
    ["merchants", "imports"].includes(page)
      ? "merchants"
      : ["send", "campaigns", "jobs", "inbox", "issues"].includes(page)
        ? "sms"
        : "system",
  );
  const item = (id: Page, icon: string, label: string) => (
    <button key={id} className={`nav-child ${page === id ? "active" : ""}`} onClick={() => setPage(id)}>
      <i>{icon}</i><span>{label}</span>
    </button>
  );
  const group = (id: "merchants" | "sms" | "system", icon: string, label: string, children: ReactNode) => (
    <div className={`nav-group ${openGroup === id ? "open" : ""}`}>
      <button className="nav-group-title" onClick={() => setOpenGroup(openGroup === id ? null : id)}>
        <i>{icon}</i><span>{label}</span><b>{openGroup === id ? "⌃" : "⌄"}</b>
      </button>
      {openGroup === id && <div className="nav-children">{children}</div>}
    </div>
  );
  return (
    <nav>
      {item("dashboard", "⌂", "نمای کلی")}
      {group("merchants", "▦", "پذیرندگان", <>{item("merchants", "⌕", "جستجو")}{item("imports", "⇧", "ورود اطلاعات پذیرندگان")}</>)}
      {group("sms", "✉", "پیامک", <>{item("send", "＋", "ارسال جدید")}{item("campaigns", "▤", "مدیریت کمپین‌ها")}{item("jobs", "◷", "روند ارسال")}{item("inbox", "↓", "پیام‌های دریافتی")}{item("issues", "!", "مغایرت‌ها")}</>)}
      {group("system", "⚙", "مدیریت سیستم", <>{isAdmin && item("users", "♙", "مدیریت کاربران")}{item("settings", "•", "تنظیمات")}</>)}
    </nav>
  );
}
function AuthPage({
  needsSetup,
  onSuccess,
}: {
  needsSetup: boolean;
  onSuccess: (user: CurrentUser) => void;
}) {
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    const form = new FormData(e.currentTarget);
    const body = needsSetup
      ? {
          username: form.get("username"),
          displayName: form.get("displayName"),
          password: form.get("password"),
          isAdmin: true,
        }
      : { username: form.get("username"), password: form.get("password") };
    try {
      const r = await fetch(
        needsSetup ? "/api/auth/setup" : "/api/auth/login",
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
        },
      );
      if (!r.ok) {
        const p = await r.json().catch(() => null);
        throw new Error(p?.message ?? "نام کاربری یا رمز عبور صحیح نیست.");
      }
      onSuccess(await r.json());
    } catch (x) {
      setError(x instanceof Error ? x.message : "خطای ناشناخته");
    } finally {
      setBusy(false);
    }
  };
  return (
    <main className="auth-page">
      <section className="auth-card">
        <Brand />
        <div>
          <span>{needsSetup ? "راه‌اندازی اولیه" : "ورود امن"}</span>
          <h1>{needsSetup ? "ایجاد مدیر سامانه" : "ورود به پنل پیامک"}</h1>
          <p>
            {needsSetup
              ? "برای اولین استفاده، حساب مدیر را ایجاد کنید."
              : "برای ادامه نام کاربری و رمز عبور را وارد کنید."}
          </p>
        </div>
        <form onSubmit={submit}>
          {needsSetup && (
            <label>
              <span>نام نمایشی</span>
              <input required name="displayName" autoComplete="name" />
            </label>
          )}
          <label>
            <span>نام کاربری</span>
            <input required name="username" autoComplete="username" />
          </label>
          <label>
            <span>رمز عبور</span>
            <input
              required
              minLength={8}
              name="password"
              type="password"
              autoComplete={needsSetup ? "new-password" : "current-password"}
            />
          </label>
          {error && <p className="form-error">{error}</p>}
          <button className="primary" disabled={busy}>
            {busy
              ? "لطفاً صبر کنید…"
              : needsSetup
                ? "ایجاد مدیر و ورود"
                : "ورود به سامانه"}
          </button>
        </form>
      </section>
    </main>
  );
}

function UsersPage() {
  const [items, setItems] = useState<ManagedUser[]>([]);
  const [error, setError] = useState("");
  const load = () =>
    fetch("/api/users")
      .then((r) => (r.ok ? r.json() : []))
      .then(setItems);
  useEffect(() => {
    load();
  }, []);
  const create = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setError("");
    const formElement = e.currentTarget;
    const form = new FormData(formElement);
    const r = await fetch("/api/users", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        username: form.get("username"),
        displayName: form.get("displayName"),
        password: form.get("password"),
        isAdmin: form.get("isAdmin") === "on",
      }),
    });
    if (!r.ok) {
      const p = await r.json().catch(() => null);
      setError(p?.message ?? "ایجاد کاربر ناموفق بود.");
      return;
    }
    formElement.reset();
    load();
  };
  const toggle = async (x: ManagedUser) => {
    const r = await fetch(`/api/users/${x.id}/status`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ isActive: !x.isActive }),
    });
    if (!r.ok) {
      const p = await r.json().catch(() => null);
      setError(p?.message ?? "تغییر وضعیت ناموفق بود.");
    }
    load();
  };
  const reset = async (x: ManagedUser) => {
    const password = prompt(`رمز عبور جدید برای ${x.displayName}`);
    if (!password) return;
    const r = await fetch(`/api/users/${x.id}/reset-password`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password }),
    });
    if (!r.ok) {
      const p = await r.json().catch(() => null);
      setError(p?.message ?? "تغییر رمز ناموفق بود.");
    }
  };
  return (
    <>
      <div className="page-title">
        <div>
          <span>امنیت و دسترسی</span>
          <h1>مدیریت کاربران</h1>
          <p>حساب‌های مجاز برای ورود به سامانه را مدیریت کنید.</p>
        </div>
      </div>
      <form className="card user-form" onSubmit={create}>
        <label>
          <span>نام نمایشی</span>
          <input required name="displayName" />
        </label>
        <label>
          <span>نام کاربری</span>
          <input required name="username" />
        </label>
        <label>
          <span>رمز عبور اولیه</span>
          <input required minLength={8} name="password" type="password" />
        </label>
        <label className="check">
          <input name="isAdmin" type="checkbox" />
          <span>
            <b>دسترسی مدیر</b>
          </span>
        </label>
        <button className="primary">ایجاد کاربر</button>
      </form>
      {error && <p className="form-error">{error}</p>}
      <section className="card users-list">
        {items.map((x) => (
          <article key={x.id}>
            <div className="user-avatar">{x.displayName.slice(0, 1)}</div>
            <span>
              <b>{x.displayName}</b>
              <small>
                {x.username} · {x.isAdmin ? "مدیر" : "کاربر عادی"}
              </small>
            </span>
            <em
              className={`status ${x.isActive ? "status-completed" : "status-invalidated"}`}
            >
              {x.isActive ? "فعال" : "غیرفعال"}
            </em>
            <button onClick={() => reset(x)}>تغییر رمز</button>
            <button onClick={() => toggle(x)}>
              {x.isActive ? "غیرفعال‌کردن" : "فعال‌کردن"}
            </button>
          </article>
        ))}
      </section>
    </>
  );
}
function DashboardPage({
  stats,
  goSend,
}: {
  stats: Dashboard;
  goSend: () => void;
}) {
  return (
    <>
      <div className="page-title">
        <div>
          <span>نمای کلی عملیات</span>
          <h1>داشبورد پیامک</h1>
          <p>وضعیت ارسال‌ها و پاسخ‌های دریافت‌شده را مشاهده کنید.</p>
        </div>
        <button className="primary" onClick={goSend}>
          ارسال پیامک جدید
        </button>
      </div>
      <section className="hero">
        <div>
          <span>کمپین جمع‌آوری شبا</span>
          <h2>فرآیند دریافت شبای پشتیبان را از اینجا شروع کنید</h2>
          <p>
            پیام‌ها در دسته‌های حداکثر صدتایی ارسال و پاسخ‌ها هر پنج دقیقه بررسی
            می‌شوند.
          </p>
          <button onClick={goSend}>ایجاد کمپین</button>
        </div>
        <div className="hero-number">
          <small>مخاطبان ثبت‌شده</small>
          <strong>{fa(stats.peopleCount)}</strong>
          <span>نفر</span>
        </div>
      </section>
      <div className="section-heading">
        <div>
          <span>آمار زنده</span>
          <h2>خلاصه عملیات</h2>
        </div>
      </div>
      <section className="stats">
        <Stat
          label="مخاطبان"
          value={stats.peopleCount}
          note="رکورد موجود در دیتابیس"
        />
        <Stat
          label="ارسال موفق"
          value={stats.sentCount}
          note="پیام ثبت‌شده نزد سرویس"
        />
        <Stat
          label="شبای دریافت‌شده"
          value={stats.receivedIbanCount}
          note="پاسخ معتبر و ثبت‌شده"
        />
        <Stat
          label="مغایرت باز"
          value={stats.openIssueCount}
          note="نیازمند بررسی اپراتور"
          alert={stats.openIssueCount > 0}
        />
      </section>
    </>
  );
}
function Stat({
  label,
  value,
  note,
  alert = false,
}: {
  label: string;
  value: number;
  note: string;
  alert?: boolean;
}) {
  return (
    <article className={`card stat ${alert ? "alert" : ""}`}>
      <span>{label}</span>
      <strong>{fa(value)}</strong>
      <small>{note}</small>
    </article>
  );
}
function SendPage({
  peopleCount,
  seed,
  onCreated,
}: {
  peopleCount: number;
  seed: CampaignSeed | null;
  onCreated: () => void;
}) {
  const [availablePeopleCount, setAvailablePeopleCount] = useState(peopleCount);
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState(seed ? `${seed.name} - کپی` : "");
  const [message, setMessage] = useState(seed?.messageTemplate ?? "");
  const [confirmationMessage, setConfirmationMessage] = useState(
    seed?.confirmationTemplate ?? "شماره شبای پشتیبان شما با پایان {{چهار_رقم_آخر_شبای_دریافتی}} با موفقیت ثبت شد.",
  );
  const [confirmationEnabled, setConfirmationEnabled] = useState(seed?.confirmationEnabled ?? false);
  const [recipientMode, setRecipientMode] = useState<"all" | "selected">("all");
  const [selectedRecipients, setSelectedRecipients] = useState<Recipient[]>([]);
  const [recipientPickerOpen, setRecipientPickerOpen] = useState(false);
  const [recipientSearch, setRecipientSearch] = useState("");
  const [recipientQuery, setRecipientQuery] = useState("");
  const [recipientResults, setRecipientResults] = useState<Recipient[]>([]);
  const [recipientTotal, setRecipientTotal] = useState(0);
  const [error, setError] = useState("");
  const [fields, setFields] = useState<TemplateField[]>([]);
  const [confirmationFields, setConfirmationFields] = useState<TemplateField[]>(
    [],
  );
  const [fieldMenuOpen, setFieldMenuOpen] = useState(false);
  const [confirmationFieldMenuOpen, setConfirmationFieldMenuOpen] =
    useState(false);
  const textRef = useRef<HTMLTextAreaElement>(null);
  const confirmationTextRef = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    Promise.all([
      fetch("/api/templates/fields"),
      fetch("/api/templates/confirmation-fields"),
    ])
      .then(async ([regular, confirmation]) => {
        setFields(regular.ok ? await regular.json() : []);
        setConfirmationFields(confirmation.ok ? await confirmation.json() : []);
      })
      .catch(() => {
        setFields([]);
        setConfirmationFields([]);
      });
  }, []);
  useEffect(() => {
    setAvailablePeopleCount(peopleCount);
  }, [peopleCount]);
  useEffect(() => {
    const loadRecipientCount = () =>
      fetch("/api/people?page=1&pageSize=10")
        .then((r) => (r.ok ? r.json() : { total: 0 }))
        .then((x) => setAvailablePeopleCount(x.total))
        .catch(() => undefined);
    loadRecipientCount();
    const timer = setInterval(loadRecipientCount, 10000);
    return () => clearInterval(timer);
  }, []);
  useEffect(() => {
    if (!recipientPickerOpen) return;
    const timer = setTimeout(() => {
      const query = new URLSearchParams({ pageSize: "100" });
      if (recipientQuery) query.set("search", recipientQuery);
      fetch(`/api/people?${query}`)
        .then((r) => (r.ok ? r.json() : { items: [], total: 0 }))
        .then((x) => {
          setRecipientResults(x.items);
          setRecipientTotal(x.total);
        })
        .catch(() => setRecipientResults([]));
    }, 250);
    return () => clearTimeout(timer);
  }, [recipientPickerOpen, recipientQuery]);
  const insertAtCursor = (
    token: string,
    input: HTMLTextAreaElement | null,
    value: string,
    setValue: (value: string) => void,
  ) => {
    if (!input) {
      setValue(value + token);
      return;
    }
    const start = input.selectionStart;
    const end = input.selectionEnd;
    setValue(value.slice(0, start) + token + value.slice(end));
    requestAnimationFrame(() => {
      input.focus();
      input.setSelectionRange(start + token.length, start + token.length);
    });
  };
  const insertField = (token: string) =>
    insertAtCursor(token, textRef.current, message, setMessage);
  const insertConfirmationField = (token: string) =>
    insertAtCursor(
      token,
      confirmationTextRef.current,
      confirmationMessage,
      setConfirmationMessage,
    );
  const preview = fields.reduce(
    (text, field) => text.replaceAll(field.token, field.sample),
    message,
  );
  const confirmationPreview = confirmationFields.reduce(
    (text, field) => text.replaceAll(field.token, field.sample),
    confirmationMessage,
  );
  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    const form = new FormData(e.currentTarget);
    try {
      const date = String(form.get("endsAtDate")).trim();
      const time = String(form.get("endsAtTime")).trim();
      if ((date && !time) || (!date && time))
        throw new Error("برای تعیین مهلت، تاریخ و ساعت را با هم وارد کنید.");
      const endsAtUtc = date && time ? jalaliDeadline(date, time) : null;
      if (endsAtUtc && new Date(endsAtUtc) <= new Date())
        throw new Error("مهلت دریافت پاسخ باید بعد از زمان فعلی باشد.");
      const body = {
        name: form.get("name"),
        messageTemplate: message,
        endsAtUtc,
        confirmationEnabled,
        confirmationTemplate: confirmationEnabled ? confirmationMessage : null,
        sendToAll: recipientMode === "all",
        recipientIds:
          recipientMode === "selected"
            ? selectedRecipients.map((x) => x.id)
            : null,
      };
      const r = await fetch("/api/campaigns", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!r.ok) {
        const problem = await r.json().catch(() => null);
        throw new Error(problem?.message ?? "ایجاد کمپین ناموفق بود.");
      }
      onCreated();
    } catch (x) {
      setError(x instanceof Error ? x.message : "خطای ناشناخته");
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <div className="page-title">
        <div>
          <span>کمپین جدید</span>
          <h1>ارسال پیامک شخصی‌سازی‌شده</h1>
          <p>
            متن را عادی بنویسید و فقط هرجا لازم بود، اطلاعات مخاطب را از فهرست
            درج کنید.
          </p>
        </div>
      </div>
      <form className="send-layout" onSubmit={submit}>
        <section className="card form-card">
          <h2>مشخصات پیام</h2>
          <label>
            <span>عنوان کمپین</span>
            <input
              required
              name="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="برای نمونه: دریافت شبای پشتیبان آبان"
            />
          </label>
          <div className="template-field">
            <div className="message-heading">
              <span>متن پیامک</span>
              <div className="field-picker">
                <button
                  type="button"
                  className="field-picker-trigger"
                  aria-expanded={fieldMenuOpen}
                  onClick={() => setFieldMenuOpen((open) => !open)}
                >
                  <span>＋</span> درج اطلاعات مخاطب <i>⌄</i>
                </button>
                {fieldMenuOpen && (
                  <div className="field-picker-menu" role="menu">
                    <div className="field-picker-title">
                      چه اطلاعاتی در متن قرار بگیرد؟
                    </div>
                    {fields.map((field) => (
                      <button
                        type="button"
                        role="menuitem"
                        key={field.name}
                        onClick={() => {
                          insertField(field.token);
                          setFieldMenuOpen(false);
                        }}
                      >
                        <span className="field-picker-icon">
                          {field.label.slice(0, 1)}
                        </span>
                        <span>
                          <b>{field.label}</b>
                          <small>نمونه: {field.sample}</small>
                        </span>
                        {field.sensitive && <em>حساس</em>}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </div>
            <textarea
              ref={textRef}
              required
              maxLength={500}
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              placeholder="متن پیام یا قالب پیام"
            />
            <small>
              برای شخصی‌سازی، نشانگر را در محل دلخواه بگذارید و یک گزینه از «درج
              اطلاعات مخاطب» انتخاب کنید. · {fa(message.length)} از ۵۰۰ نویسه
            </small>
          </div>
          <p className="privacy-note">
            از درج کد ملی، موبایل یا شبای کامل در متن پیام مگر در صورت ضرورت
            خودداری کنید.
          </p>
          <div className="recipient-section">
            <span>گیرندگان پیامک</span>
            <div className="recipient-options">
              <label className={recipientMode === "all" ? "active" : ""}>
                <input
                  type="radio"
                  name="recipientMode"
                  checked={recipientMode === "all"}
                  onChange={() => setRecipientMode("all")}
                />
                <span>
                  <b>همه مخاطبان</b>
                  <small>{fa(availablePeopleCount)} مخاطب قابل ارسال</small>
                </span>
              </label>
              <label className={recipientMode === "selected" ? "active" : ""}>
                <input
                  type="radio"
                  name="recipientMode"
                  checked={recipientMode === "selected"}
                  onChange={() => setRecipientMode("selected")}
                />
                <span>
                  <b>فهرست انتخابی</b>
                  <small>{fa(selectedRecipients.length)} نفر انتخاب شده</small>
                </span>
              </label>
            </div>
            {recipientMode === "selected" && (
              <div className="selected-recipients">
                <button
                  type="button"
                  onClick={() => setRecipientPickerOpen(true)}
                >
                  جست‌وجو و انتخاب مخاطب
                </button>
                {selectedRecipients.map((person) => (
                  <span key={person.id}>
                    <span className="selected-recipient-info">
                      <b>{person.firstName} {person.lastName}</b>
                      <small>{person.mobile}</small>
                    </span>
                    <button
                      type="button"
                      onClick={() =>
                        setSelectedRecipients((items) =>
                          items.filter((x) => x.id !== person.id),
                        )
                      }
                    >
                      ×
                    </button>
                  </span>
                ))}
              </div>
            )}
            {recipientMode === "all" && availablePeopleCount === 0 && (
              <small className="recipient-warning">
                هنوز مخاطب دارای شماره موبایل ثبت نشده است.
              </small>
            )}
          </div>
          <div className="deadline-field">
            <span>
              مهلت دریافت پاسخ <em>اختیاری</em>
            </span>
            <div>
              <label>
                <small>تاریخ شمسی</small>
                <input
                  name="endsAtDate"
                  inputMode="numeric"
                  placeholder="۱۴۰۵/۰۸/۲۰"
                />
              </label>
              <label>
                <small>ساعت</small>
                <input name="endsAtTime" type="time" />
              </label>
            </div>
            <small>
              اگر خالی بماند، کمپین بدون تاریخ انقضا فعال خواهد بود.
            </small>
          </div>
          <label className="check">
            <input
              name="confirmation"
              type="checkbox"
              checked={confirmationEnabled}
              onChange={(e) => setConfirmationEnabled(e.target.checked)}
            />
            <span>
              <b>ارسال پیام تأیید پس از ثبت شبا</b>
              <small>
                این گزینه به‌صورت پیش‌فرض خاموش است و هزینه پیامک دارد.
              </small>
            </span>
          </label>
          {confirmationEnabled && (
            <div className="template-field confirmation-template">
              <div className="message-heading">
                <span>متن پیام تأیید</span>
                <div className="field-picker">
                  <button
                    type="button"
                    className="field-picker-trigger"
                    aria-expanded={confirmationFieldMenuOpen}
                    onClick={() =>
                      setConfirmationFieldMenuOpen((open) => !open)
                    }
                  >
                    <span>＋</span> درج اطلاعات <i>⌄</i>
                  </button>
                  {confirmationFieldMenuOpen && (
                    <div className="field-picker-menu" role="menu">
                      <div className="field-picker-title">
                        چه اطلاعاتی در متن قرار بگیرد؟
                      </div>
                      {confirmationFields.map((field) => (
                        <button
                          type="button"
                          role="menuitem"
                          key={field.name}
                          onClick={() => {
                            insertConfirmationField(field.token);
                            setConfirmationFieldMenuOpen(false);
                          }}
                        >
                          <span className="field-picker-icon">
                            {field.label.slice(0, 1)}
                          </span>
                          <span>
                            <b>{field.label}</b>
                            <small>نمونه: {field.sample}</small>
                          </span>
                          {field.sensitive && <em>حساس</em>}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </div>
              <textarea
                ref={confirmationTextRef}
                required
                maxLength={500}
                value={confirmationMessage}
                onChange={(e) => setConfirmationMessage(e.target.value)}
              />
              <small>{fa(confirmationMessage.length)} از ۵۰۰ نویسه</small>
            </div>
          )}
          {error && <p className="form-error">{error}</p>}
          <button
            className="primary submit"
            disabled={
              busy ||
              (recipientMode === "all"
                ? availablePeopleCount === 0
                : selectedRecipients.length === 0)
            }
          >
            {busy ? "در حال ثبت…" : "ذخیره در وضعیت آماده ارسال"}
          </button>
        </section>
        <aside className="card preview">
          <span>پیش‌نمایش برای یک مخاطب نمونه</span>
          <div className="phone">
            <small>پیامک درخواست</small>
            <p>{preview || "متن پیامک شما در این قسمت نمایش داده می‌شود."}</p>
          </div>
          {confirmationEnabled && (
            <div className="phone confirmation-phone">
              <small>پیامک تأیید</small>
              <p>{confirmationPreview}</p>
            </div>
          )}
          <dl>
            <div>
              <dt>تعداد گیرنده</dt>
              <dd>
                {fa(
                  recipientMode === "all"
                    ? availablePeopleCount
                    : selectedRecipients.length,
                )}
              </dd>
            </div>
            <div>
              <dt>نوع ارسال</dt>
              <dd>شخصی‌سازی‌شده</dd>
            </div>
            <div>
              <dt>پیام تأیید</dt>
              <dd>{confirmationEnabled ? "فعال" : "غیرفعال"}</dd>
            </div>
          </dl>
        </aside>
      </form>
      {recipientPickerOpen && (
        <div className="modal-backdrop" role="presentation">
          <section
            className="recipient-modal"
            role="dialog"
            aria-modal="true"
            aria-label="انتخاب مخاطبان"
          >
            <header>
              <div>
                <h2>انتخاب مخاطبان</h2>
                <small>{fa(recipientTotal)} نتیجه</small>
              </div>
              <button type="button" onClick={() => setRecipientPickerOpen(false)}>
                ×
              </button>
            </header>
            <div className="modal-search">
              <input
                autoFocus
                value={recipientSearch}
                onChange={(e) => setRecipientSearch(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && setRecipientQuery(recipientSearch.trim())}
                placeholder="نام، کد ملی یا شماره موبایل را وارد کنید"
                inputMode="search"
              />
              <button className="primary" type="button" onClick={() => setRecipientQuery(recipientSearch.trim())}>جستجو</button>
            </div>
            <div className="recipient-results">
              {recipientResults.length === 0 ? (
                <div className="empty"><h3>مخاطبی یافت نشد</h3></div>
              ) : (
                recipientResults.map((person) => {
                  const selected = selectedRecipients.some(
                    (x) => x.id === person.id,
                  );
                  return (
                    <button
                      type="button"
                      className={selected ? "selected" : ""}
                      key={person.id}
                      onClick={() =>
                        setSelectedRecipients((items) =>
                          selected
                            ? items.filter((x) => x.id !== person.id)
                            : [...items, person],
                        )
                      }
                    >
                      <i>{selected ? "✓" : "+"}</i>
                      <span>
                        <b>{person.firstName} {person.lastName}</b>
                        <small>{person.mobile} · {person.nationalCode}</small>
                      </span>
                    </button>
                  );
                })
              )}
            </div>
            <footer>
              <span>{fa(selectedRecipients.length)} نفر انتخاب شده</span>
              <button
                className="primary"
                type="button"
                onClick={() => setRecipientPickerOpen(false)}
              >
                تأیید فهرست
              </button>
            </footer>
          </section>
        </div>
      )}
    </>
  );
}

function MerchantsPage() {
  const [items, setItems] = useState<Merchant[]>([]);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [submittedSearch, setSubmittedSearch] = useState("");
  const [page, setPage] = useState(1);
  const [showForm, setShowForm] = useState(false);
  const [error, setError] = useState("");
  const pageSize = 30;
  const load = () => {
    const q = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
    });
    if (submittedSearch) q.set("search", submittedSearch);
    fetch(`/api/merchants?${q}`)
      .then((r) => (r.ok ? r.json() : { items: [], total: 0 }))
      .then((x) => {
        setItems(x.items);
        setTotal(x.total);
      })
      .catch(() => setItems([]));
  };
  useEffect(() => { load(); }, [submittedSearch, page]);
  const searchMerchants = () => { setPage(1); setSubmittedSearch(search.trim()); };
  const create = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setError("");
    const formElement = e.currentTarget;
    const form = new FormData(formElement);
    const body = {
      terminalNumber: form.get("terminalNumber"),
      nationalId: form.get("nationalId"),
      merchantName: form.get("merchantName"),
      mobile: form.get("mobile"),
      status: form.get("status"),
      currentIban: form.get("currentIban"),
      bank: form.get("bank"),
      agency: form.get("agency"),
    };
    const r = await fetch("/api/merchants", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!r.ok) {
      const p = await r.json().catch(() => null);
      setError(p?.message ?? "ثبت پذیرنده ناموفق بود.");
      return;
    }
    formElement.reset();
    setShowForm(false);
    setPage(1);
    load();
  };
  const pages = Math.max(1, Math.ceil(total / pageSize));
  return (
    <>
      <div className="page-title">
        <div>
          <span>دفتر پذیرندگان</span>
          <h1>جست‌وجوی پذیرندگان</h1>
          <p>
            اطلاعات فایل‌های واردشده و پذیرندگان ثبت‌شده دستی را مشاهده کنید.
          </p>
        </div>
        <button className="primary" onClick={() => setShowForm((x) => !x)}>
          {showForm ? "بستن فرم" : "ثبت دستی پذیرنده"}
        </button>
      </div>
      {showForm && (
        <form className="card merchant-form" onSubmit={create}>
          <label>
            <span>شماره پایانه</span>
            <input required name="terminalNumber" />
          </label>
          <label>
            <span>شناسه ملی</span>
            <input required name="nationalId" />
          </label>
          <label>
            <span>نام پذیرنده</span>
            <input required name="merchantName" />
          </label>
          <label>
            <span>وضعیت</span>
            <input name="status" />
          </label>
          <label>
            <span>شماره موبایل</span>
            <input name="mobile" inputMode="tel" placeholder="۰۹۱۲۱۲۳۴۵۶۷" />
          </label>
          <label>
            <span>شماره شبا فعلی</span>
            <input name="currentIban" dir="ltr" />
          </label>
          <label>
            <span>بانک</span>
            <input name="bank" />
          </label>
          <label>
            <span>نمایندگی</span>
            <input name="agency" />
          </label>
          <button className="primary">ثبت پذیرنده</button>
        </form>
      )}
      {error && <p className="form-error">{error}</p>}
      <section className="card merchant-search">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && searchMerchants()}
          placeholder="جست‌وجوی پایانه، شناسه ملی، نام، بانک یا نمایندگی"
        />
        <button className="primary" onClick={searchMerchants}>جستجو</button>
        <span>{fa(total)} پذیرنده</span>
      </section>
      <section className="card merchant-table">
        <div className="merchant-head">
          <span>پذیرنده</span>
          <span>پایانه و شناسه ملی</span>
          <span>بانک و نمایندگی</span>
          <span>وضعیت اتصال</span>
        </div>
        {items.length === 0 ? (
          <div className="empty">
            <h3>پذیرنده‌ای یافت نشد</h3>
          </div>
        ) : (
          items.map((x) => (
            <article key={x.id}>
              <span>
                <b>{x.merchantName}</b>
                <small>{x.status || "بدون وضعیت"}</small>
              </span>
              <span>
                <b>{x.terminalNumber}</b>
                <small>
                  {x.nationalId}{x.mobile ? ` · ${x.mobile}` : ""}
                </small>
              </span>
              <span>
                <b>{x.bank || "—"}</b>
                <small>{x.agency || "—"}</small>
              </span>
              <em
                className={`status ${x.hasMobile ? "status-completed" : "status-failed"}`}
              >
                {x.hasMobile ? "دارای شماره موبایل" : "بدون شماره موبایل"}
              </em>
            </article>
          ))
        )}
        <div className="pagination">
          <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            صفحه قبل
          </button>
          <span>
            صفحه {fa(page)} از {fa(pages)}
          </span>
          <button
            disabled={page >= pages}
            onClick={() => setPage((p) => p + 1)}
          >
            صفحه بعد
          </button>
        </div>
      </section>
    </>
  );
}

const importStatus: Record<string, string> = {
  Queued: "در صف",
  Validating: "اعتبارسنجی",
  Importing: "در حال درج",
  Completed: "تکمیل‌شده",
  Failed: "ناموفق",
  Invalidated: "ابطال‌شده",
};
function ImportsPage() {
  const [items, setItems] = useState<ImportBatch[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const load = () =>
    fetch("/api/merchant-imports")
      .then((r) => (r.ok ? r.json() : []))
      .then(setItems)
      .catch(() => setItems([]));
  useEffect(() => {
    load();
    const timer = setInterval(load, 3000);
    return () => clearInterval(timer);
  }, []);
  const upload = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    const formElement = e.currentTarget;
    const form = new FormData(formElement);
    try {
      const r = await fetch("/api/merchant-imports", {
        method: "POST",
        body: form,
      });
      if (!r.ok) {
        const problem = await r.json().catch(() => null);
        throw new Error(problem?.message ?? "بارگذاری فایل ناموفق بود.");
      }
      formElement.reset();
      load();
    } catch (x) {
      setError(x instanceof Error ? x.message : "خطای ناشناخته");
    } finally {
      setBusy(false);
    }
  };
  const invalidate = async (id: number) => {
    if (!confirm("این فایل ابطال و اطلاعات پذیرندگان آن حذف شود؟")) return;
    const r = await fetch(`/api/merchant-imports/${id}`, { method: "DELETE" });
    if (!r.ok) {
      const problem = await r.json().catch(() => null);
      setError(problem?.message ?? "ابطال فایل ناموفق بود.");
    }
    load();
  };
  return (
    <>
      <div className="page-title">
        <div>
          <span>اطلاعات پذیرندگان</span>
          <h1>ورود فایل اکسل</h1>
          <p>
            فایل‌های حجیم در پس‌زمینه پردازش می‌شوند و روند درج زنده نمایش داده
            می‌شود.
          </p>
        </div>
      </div>
      <form className="card upload-card" onSubmit={upload}>
        <div>
          <b>فایل جدید</b>
          <small>فرمت مجاز: اکسل با پسوند xlsx</small>
        </div>
        <input required type="file" name="file" accept=".xlsx" />
        <button className="primary" disabled={busy}>
          {busy ? "در حال بارگذاری…" : "بارگذاری و شروع پردازش"}
        </button>
      </form>
      {error && <p className="form-error">{error}</p>}
      <section className="card import-history-card">
        <div className="list-head"><div><b>سابقه فایل‌ها</b><small>وضعیت پردازش و نتیجه هر بارگذاری</small></div><span>{fa(items.length)} فایل</span></div>
        {items.length === 0 ? <div className="empty"><h3>هنوز فایلی بارگذاری نشده است</h3></div> : (
          <div className="table-scroll"><table className="history-table"><thead><tr><th>فایل و زمان</th><th>وضعیت و پیشرفت</th><th>کل ردیف</th><th>درج‌شده</th><th>تکراری</th><th>نامعتبر</th><th>عملیات</th></tr></thead><tbody>
            {items.map((x) => { const percent=x.totalRows?Math.round(x.processedRows*100/x.totalRows):0; const running=["Queued","Validating","Importing"].includes(x.status); return <tr key={x.id}>
              <td><b>{x.originalFileName}</b><small>{new Date(x.createdAtUtc).toLocaleString("fa-IR")}</small></td>
              <td className="history-status"><em className={`status status-${x.status.toLowerCase()}`}>{importStatus[x.status]??x.status}</em>{running&&<><div className="progress"><i style={{width:`${percent}%`}}/></div><small>{x.currentStage??"در صف پردازش"} — {fa(x.processedRows)} از {fa(x.totalRows)}</small></>}{x.lastError&&<small className="row-error">{x.lastError}</small>}</td>
              <td>{fa(x.totalRows)}</td><td>{fa(x.insertedRows)}</td><td>{fa(x.duplicateRows)}</td><td>{fa(x.invalidRows)}</td>
              <td>{!["Importing","Validating","Queued","Invalidated"].includes(x.status)?<button className="table-action danger" onClick={()=>invalidate(x.id)}>ابطال</button>:<span>—</span>}</td>
            </tr>})}
          </tbody></table></div>
        )}
      </section>
    </>
  );
}

const campaignStatusLabels: Record<string, string> = {
  Draft: "آماده ارسال",
  Active: "در حال ارسال",
  Closed: "پایان‌یافته",
};
function CampaignsPage({ onReuse }: { onReuse: (campaign: CampaignSeed) => void }) {
  const [items, setItems] = useState<CampaignSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [status, setStatus] = useState("");
  const [error, setError] = useState("");
  const [editing, setEditing] = useState<CampaignSummary | null>(null);
  const [viewing, setViewing] = useState<CampaignSummary | null>(null);
  const [editName, setEditName] = useState("");
  const [editMessage, setEditMessage] = useState("");
  const [editConfirmationEnabled, setEditConfirmationEnabled] = useState(false);
  const [editConfirmation, setEditConfirmation] = useState("");
  const [editDate, setEditDate] = useState("");
  const [editTime, setEditTime] = useState("");
  const [fields, setFields] = useState<TemplateField[]>([]);
  const load = () => {
    const query = new URLSearchParams({ pageSize: "100" });
    if (appliedSearch) query.set("search", appliedSearch);
    if (status) query.set("status", status);
    fetch(`/api/campaigns?${query}`)
      .then((r) => (r.ok ? r.json() : { items: [], total: 0 }))
      .then((x) => { setItems(x.items); setTotal(x.total); })
      .catch(() => setItems([]));
  };
  useEffect(load, [appliedSearch, status]);
  useEffect(() => { fetch("/api/templates/fields").then((r) => r.ok ? r.json() : []).then(setFields); }, []);
  const openEdit = (item: CampaignSummary) => {
    const deadline = jalaliParts(item.endsAtUtc);
    setEditing(item); setEditName(item.name); setEditMessage(item.messageTemplate);
    setEditConfirmationEnabled(item.confirmationEnabled); setEditConfirmation(item.confirmationTemplate ?? "");
    setEditDate(deadline.date); setEditTime(deadline.time); setError("");
  };
  const save = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault(); if (!editing) return; setError("");
    try {
      if ((editDate && !editTime) || (!editDate && editTime)) throw new Error("تاریخ و ساعت مهلت را با هم وارد کنید.");
      const r = await fetch(`/api/campaigns/${editing.id}`, {
        method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name: editName, messageTemplate: editMessage, endsAtUtc: editDate ? jalaliDeadline(editDate, editTime) : null, confirmationEnabled: editConfirmationEnabled, confirmationTemplate: editConfirmationEnabled ? editConfirmation : null }),
      });
      if (!r.ok) { const p = await r.json().catch(() => null); throw new Error(p?.message ?? "ویرایش کمپین ناموفق بود."); }
      setEditing(null); load();
    } catch (x) { setError(x instanceof Error ? x.message : "خطای ناشناخته"); }
  };
  const start = async (item: CampaignSummary) => {
    if (!confirm(`ارسال کمپین «${item.name}» برای ${fa(item.recipientCount)} گیرنده شروع شود؟`)) return;
    setError("");
    const r = await fetch(`/api/campaigns/${item.id}/start`, { method: "POST" });
    if (!r.ok) { const p = await r.json().catch(() => null); setError(p?.message ?? "شروع ارسال ناموفق بود."); return; }
    load();
  };
  return <>
    <div className="page-title"><div><span>مدیریت ارسال</span><h1>جست‌وجو و ویرایش کمپین‌ها</h1><p>کمپین را بررسی و ویرایش کنید؛ ارسال فقط با فرمان شما شروع می‌شود.</p></div></div>
    <section className="card campaign-filters">
      <input value={search} onChange={(e)=>setSearch(e.target.value)} onKeyDown={(e)=>e.key==="Enter"&&setAppliedSearch(search.trim())} placeholder="جست‌وجوی عنوان یا متن کمپین" />
      <select value={status} onChange={(e)=>setStatus(e.target.value)}><option value="">همه وضعیت‌ها</option><option value="Draft">آماده ارسال</option><option value="Active">در حال ارسال</option><option value="Closed">پایان‌یافته</option></select>
      <button className="primary" onClick={()=>setAppliedSearch(search.trim())}>جستجو</button>
      <span>{fa(total)} کمپین</span>
    </section>
    {error && <p className="form-error">{error}</p>}
    <section className="card campaign-list">
      {items.length===0?<div className="empty"><h3>کمپینی یافت نشد</h3></div>:items.map((x)=><article key={x.id}>
        <div><b>{x.name}</b><small>{x.messageTemplate}</small></div>
        <span><em className={`status status-${x.status.toLowerCase()}`}>{campaignStatusLabels[x.status]??x.status}</em><small>{fa(x.recipientCount)} گیرنده · {fa(x.sentCount)} موفق · {fa(x.failedCount)} خطا</small></span>
        <div className="campaign-actions"><button onClick={()=>setViewing(x)}>مشاهده</button><button onClick={()=>openEdit(x)} disabled={x.sentCount>0||x.status==="Closed"}>ویرایش</button>{x.status==="Draft"&&<button className="primary" onClick={()=>start(x)}>شروع ارسال</button>}</div>
      </article>)}
    </section>
    {viewing&&<div className="modal-backdrop"><section className="campaign-editor campaign-viewer">
      <header><div><h2>مشاهده کمپین</h2><small>{campaignStatusLabels[viewing.status]??viewing.status} · {fa(viewing.recipientCount)} گیرنده</small></div><button type="button" onClick={()=>setViewing(null)}>×</button></header>
      <div className="campaign-view-field"><span>عنوان کمپین</span><b>{viewing.name}</b></div>
      <div className="campaign-view-field"><span>متن یا قالب پیام</span><pre>{viewing.messageTemplate}</pre></div>
      <div className="campaign-view-field"><span>پیام تأیید</span><pre>{viewing.confirmationEnabled ? viewing.confirmationTemplate || "—" : "غیرفعال"}</pre></div>
      <div className="campaign-view-stats"><span>{fa(viewing.sentCount)} ارسال موفق</span><span>{fa(viewing.failedCount)} خطا</span></div>
      <footer><button type="button" onClick={()=>setViewing(null)}>بستن</button><button type="button" className="primary" onClick={()=>onReuse(viewing)}>استفاده در کمپین جدید</button></footer>
    </section></div>}
    {editing&&<div className="modal-backdrop"><form className="campaign-editor" onSubmit={save}>
      <header><div><h2>ویرایش کمپین</h2><small>پس از ذخیره، کمپین در وضعیت آماده ارسال می‌ماند.</small></div><button type="button" onClick={()=>setEditing(null)}>×</button></header>
      <label><span>عنوان کمپین</span><input required value={editName} onChange={(e)=>setEditName(e.target.value)} /></label>
      <label><span>متن یا قالب پیام</span><textarea required maxLength={1000} value={editMessage} onChange={(e)=>setEditMessage(e.target.value)} /></label>
      <div className="field-chips">{fields.map((f)=><button type="button" key={f.name} onClick={()=>setEditMessage((v)=>v+f.token)}>{f.label}</button>)}</div>
      <div className="editor-deadline"><label><span>تاریخ شمسی مهلت</span><input value={editDate} onChange={(e)=>setEditDate(e.target.value)} placeholder="۱۴۰۵/۰۸/۲۰" /></label><label><span>ساعت</span><input type="time" value={editTime} onChange={(e)=>setEditTime(e.target.value)} /></label></div>
      <label className="check"><input type="checkbox" checked={editConfirmationEnabled} onChange={(e)=>setEditConfirmationEnabled(e.target.checked)} /><span><b>ارسال پیام تأیید</b></span></label>
      {editConfirmationEnabled&&<label><span>قالب پیام تأیید</span><textarea required value={editConfirmation} onChange={(e)=>setEditConfirmation(e.target.value)} /></label>}
      <footer><button type="button" onClick={()=>setEditing(null)}>انصراف</button><button className="primary">ذخیره تغییرات</button></footer>
    </form></div>}
  </>;
}

function JobsPage() {
  const [items, setItems] = useState<CampaignJob[]>([]);
  const [failedCampaign, setFailedCampaign] = useState<CampaignJob | null>(null);
  const [failedItems, setFailedItems] = useState<FailedOutboundMessage[]>([]);
  const [failedTotal, setFailedTotal] = useState(0);
  const [failedPage, setFailedPage] = useState(1);
  const failedPageSize = 50;
  const load = () =>
    fetch("/api/campaigns/jobs")
      .then((r) => (r.ok ? r.json() : []))
      .then(setItems)
      .catch(() => setItems([]));
  useEffect(() => {
    load();
    const timer = setInterval(load, 5000);
    return () => clearInterval(timer);
  }, []);
  const retry = async (id: number) => {
    await fetch(`/api/campaigns/${id}/retry-failed`, { method: "POST" });
    load();
  };
  useEffect(() => {
    if (!failedCampaign) return;
    fetch(`/api/campaigns/${failedCampaign.id}/failed-messages?page=${failedPage}&pageSize=${failedPageSize}`)
      .then((r) => r.ok ? r.json() : { items: [], total: 0 })
      .then((x) => { setFailedItems(x.items); setFailedTotal(x.total); })
      .catch(() => { setFailedItems([]); setFailedTotal(0); });
  }, [failedCampaign, failedPage]);
  return (
    <>
      <div className="page-title">
        <div>
          <span>صف ارسال</span>
          <h1>روند ارسال پیامک‌ها</h1>
          <p>
            پیشرفت هر کمپین و پیام‌های خطادار را مشاهده و مجدداً ارسال کنید.
          </p>
        </div>
      </div>
      <section className="card data-list">
        {items.length === 0 ? (
          <div className="empty">
            <h3>هنوز کمپینی ایجاد نشده است</h3>
          </div>
        ) : (
          items.map((x) => {
            const percent = x.total ? Math.round((x.sent / x.total) * 100) : 0;
            return (
              <article className="job-row" key={x.id}>
                <div className="row-main">
                  <span>
                    <b>{x.name}</b>
                    <small>
                      {new Date(x.startsAtUtc).toLocaleString("fa-IR")}
                    </small>
                  </span>
                  <strong>{fa(percent)}٪</strong>
                </div>
                <div className="progress">
                  <i style={{ width: `${percent}%` }} />
                </div>
                <div className="job-stats">
                  <span>
                    کل <b>{fa(x.total)}</b>
                  </span>
                  <span>
                    در صف <b>{fa(x.pending)}</b>
                  </span>
                  <span>
                    در حال ارسال <b>{fa(x.processing)}</b>
                  </span>
                  <span className="ok">
                    موفق <b>{fa(x.sent)}</b>
                  </span>
                  <span className="bad">
                    خطا <b>{fa(x.failed)}</b>
                  </span>
                  {x.failed > 0 && (
                    <><button onClick={() => { setFailedCampaign(x); setFailedPage(1); }}>مشاهده خطاها</button><button onClick={() => retry(x.id)}>تلاش مجدد خطاها</button></>
                  )}
                </div>
              </article>
            );
          })
        )}
      </section>
      {failedCampaign&&<div className="modal-backdrop"><section className="campaign-editor failed-deliveries">
        <header><div><h2>افراد ارسال‌نشده</h2><small>{failedCampaign.name} · {fa(failedTotal)} خطا</small></div><button type="button" onClick={()=>setFailedCampaign(null)}>×</button></header>
        {failedItems.length===0?<div className="empty"><h3>خطایی یافت نشد</h3></div>:<div className="table-scroll"><table><thead><tr><th>نام مخاطب</th><th>شماره موبایل</th><th>تعداد تلاش</th><th>علت خطا</th></tr></thead><tbody>{failedItems.map((item)=><tr key={item.id}><td>{item.fullName}</td><td className="ltr-cell">{item.mobile}</td><td>{fa(item.attemptCount)}</td><td className="failed-reason">{item.error}</td></tr>)}</tbody></table></div>}
        <div className="pagination"><button disabled={failedPage===1} onClick={()=>setFailedPage((p)=>p-1)}>قبلی</button><span>صفحه {fa(failedPage)} از {fa(Math.max(1,Math.ceil(failedTotal/failedPageSize)))}</span><button disabled={failedPage*failedPageSize>=failedTotal} onClick={()=>setFailedPage((p)=>p+1)}>بعدی</button></div>
        <footer><button type="button" onClick={()=>setFailedCampaign(null)}>بستن</button></footer>
      </section></div>}
    </>
  );
}

function InboxPage() {
  const [items, setItems] = useState<Inbound[]>([]);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [issueType, setIssueType] = useState("");
  const [appliedSearch, setAppliedSearch] = useState("");
  const [appliedStatus, setAppliedStatus] = useState("");
  const [appliedIssueType, setAppliedIssueType] = useState("");
  const [fromDate, setFromDate] = useState("");
  const [appliedFromUtc, setAppliedFromUtc] = useState("");
  const [dateError, setDateError] = useState("");
  const [dateInitialized, setDateInitialized] = useState(false);
  const [page, setPage] = useState(1);
  const pageSize = 50;
  const load = () => {
    const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (appliedSearch) q.set("search", appliedSearch);
    if (appliedStatus) q.set("status", appliedStatus);
    if (appliedIssueType) q.set("issueType", appliedIssueType);
    if (appliedFromUtc) q.set("fromUtc", appliedFromUtc);
    fetch(`/api/inbound-messages?${q}`)
      .then((r) => (r.ok ? r.json() : { items: [], total: 0 }))
      .then((x) => {
        setItems(x.items);
        setTotal(x.total);
      })
      .catch(() => setItems([]));
  };
  useEffect(() => {
    fetch("/api/campaigns/latest-start")
      .then((r) => r.ok ? r.json() : { startsAtUtc: null })
      .then((x) => { if (x.startsAtUtc) { setFromDate(jalaliParts(x.startsAtUtc).date); setAppliedFromUtc(x.startsAtUtc); } })
      .finally(() => setDateInitialized(true));
  }, []);
  useEffect(() => {
    if (!dateInitialized) return;
    load();
    const timer = setInterval(load, 15000);
    return () => clearInterval(timer);
  }, [appliedSearch, appliedStatus, appliedIssueType, appliedFromUtc, page, dateInitialized]);
  const searchInbox = () => {
    try { setDateError(""); setPage(1); setAppliedSearch(search.trim()); setAppliedStatus(status); setAppliedIssueType(issueType); setAppliedFromUtc(fromDate.trim() ? jalaliDeadline(fromDate, "00:00") : ""); }
    catch { setDateError("تاریخ شروع را به‌صورت شمسی و در قالب ۱۴۰۵/۰۸/۲۰ وارد کنید."); }
  };
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const exportQuery = new URLSearchParams();
  if (appliedSearch) exportQuery.set("search", appliedSearch);
  if (appliedStatus) exportQuery.set("status", appliedStatus);
  if (appliedIssueType) exportQuery.set("issueType", appliedIssueType);
  if (appliedFromUtc) exportQuery.set("fromUtc", appliedFromUtc);
  const inboundExcelHref = `/api/inbound-messages/export.xlsx?${exportQuery}`;
  return (
    <>
      <div className="page-title">
        <div>
          <span>صندوق ورودی</span>
          <h1>پیام‌های دریافتی</h1>
          <p>پاسخ‌ها را جست‌وجو و بر اساس نتیجه پردازش فیلتر کنید.</p>
        </div>
        <div className="export-actions">
          <a href={inboundExcelHref}>خروجی اکسل پیام‌ها</a>
          <a href="/api/secondary-accounts/export.xlsx">اکسل شباهای ثبت‌شده</a>
          <a href="/api/secondary-accounts/export.csv">CSV شباهای ثبت‌شده</a>
        </div>
      </div>
      <section className="card filters">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && searchInbox()}
          placeholder="جست‌وجوی موبایل، متن یا شناسه پیام"
        />
        <label className="inbox-from-date"><span>از تاریخ</span><input value={fromDate} onChange={(e)=>setFromDate(e.target.value)} placeholder="۱۴۰۵/۰۸/۲۰" inputMode="numeric" /></label>
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">همه وضعیت‌ها</option>
          <option value="Processed">پاسخ موفق</option>
          <option value="ReconciliationRequired">نیازمند بررسی</option>
          <option value="Pending">در انتظار پردازش</option>
          <option value="Failed">خطای پردازش</option>
        </select>
        <select value={issueType} onChange={(e)=>setIssueType(e.target.value)}>
          <option value="">همه دلایل بررسی</option>
          {Object.entries(issueLabels).map(([value,label])=><option key={value} value={value}>{label}</option>)}
        </select>
        <button className="primary" onClick={searchInbox}>جستجو</button>
        <button onClick={load}>به‌روزرسانی نتایج</button>
        <span>{fa(total)} پیام</span>
      </section>
      {dateError&&<p className="form-error">{dateError}</p>}
      <section className="card inbox-list">
        {items.length === 0 ? (
          <div className="empty">
            <h3>پیامی یافت نشد</h3>
          </div>
        ) : (
          items.map((x) => (
            <article key={x.id}>
              <div>
                <b>{x.senderMobile}</b>
                <small>
                  ردیف {fa(x.id)} · {new Date(x.receivedAtUtc).toLocaleString("fa-IR")}
                </small>
              </div>
              <p>{x.rawText}</p>
              <span
                className={`status status-${x.processingStatus.toLowerCase()}`}
              >
                {x.processingStatus === "Processed"
                  ? "پاسخ موفق"
                  : x.processingStatus === "ReconciliationRequired"
                    ? `نیازمند بررسی — ${issueLabels[x.issueType ?? ""] ?? "علت نامشخص"}`
                    : x.processingStatus === "Pending"
                      ? "در انتظار"
                      : "خطا"}
              </span>
              {x.iban && <code>{x.iban}</code>}
            </article>
          ))
        )}
        <div className="pagination">
          <button disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>صفحه قبل</button>
          <span>صفحه {fa(page)} از {fa(pages)} · {fa(total)} پیام</span>
          <button disabled={page >= pages} onClick={() => setPage((value) => value + 1)}>صفحه بعد</button>
        </div>
      </section>
    </>
  );
}

function IssuesPage({ issues }: { issues: Issue[] }) {
  return (
    <>
      <div className="page-title">
        <div>
          <span>کنترل داده‌ها</span>
          <h1>جدول مغایرت‌ها</h1>
          <p>پاسخ‌های نامعتبر یا مبهم برای بررسی دستی.</p>
        </div>
      </div>
      <section className="card table-card">
        {issues.length === 0 ? (
          <div className="empty">
            <b>✓</b>
            <h3>مغایرت بازی وجود ندارد</h3>
          </div>
        ) : (
          issues.map((x) => (
            <div className="issue" key={x.id}>
              <span>
                <b>{x.senderMobile}</b>
                <small>{issueLabels[x.issueType] ?? x.issueType}</small>
              </span>
              <code>{x.extractedIban ?? "بدون شبا"}</code>
              <button>بررسی</button>
            </div>
          ))
        )}
      </section>
    </>
  );
}
function SettingsPage() {
  return (
    <>
      <div className="page-title">
        <div>
          <span>پیکربندی</span>
          <h1>تنظیمات سامانه</h1>
          <p>
            اعتبارنامه‌ها خارج از رابط و در مخزن امن تنظیمات نگهداری می‌شوند.
          </p>
        </div>
      </div>
      <section className="card settings">
        <b>فاصله واکشی پیام‌های ورودی</b>
        <strong>۵ دقیقه</strong>
        <small>پیام تأیید برای هر کمپین و در زمان ایجاد آن تنظیم می‌شود.</small>
      </section>
    </>
  );
}
export default App;
