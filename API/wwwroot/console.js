"use strict";

const byId = (id) => document.getElementById(id);
const routes = { registration: "/", monitor: "/monitor" };
const stateLabels = {
    queued: "อยู่ในคิว",
    running: "กำลังทำงาน",
    awaitingOtp: "รอ OTP",
    succeeded: "สำเร็จ",
    failed: "ล้มเหลว",
    expired: "หมดเวลา",
    interrupted: "หยุดกลางทาง",
};
const stageLabels = {
    openSession: "เปิดเซสชันสมัคร",
    checkRegistrationMethod: "ตรวจวิธีสมัคร",
    requestOtp: "ขอรหัส OTP",
    awaitingOtp: "รอ OTP จากผู้ใช้",
    verifyOtp: "ตรวจ OTP",
    validateProfile: "ตรวจชื่อโปรไฟล์",
    setPassword: "ตั้งรหัสผ่าน LINE",
    registerAccount: "สร้างบัญชี LINE",
    syncContacts: "เพิ่มเพื่อน",
    createGroup: "สร้างกลุ่ม",
    done: "เสร็จสิ้น",
};
const kindLabels = { registration: "สมัครบัญชี", addFriends: "เพิ่มเพื่อน", createGroup: "สร้างกลุ่ม" };
const errorLabels = {
    invalidCredentials: "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง",
    accountBusy: "มีงานของบัญชีหรือเบอร์นี้อยู่ในคิวแล้ว",
    queueFull: "คิวเต็ม กรุณาลองอีกครั้งภายหลัง",
    otpExpired: "รหัส OTP หมดอายุแล้ว",
    jobNotAwaitingOtp: "งานนี้ไม่ได้อยู่ในขั้นตอนรอ OTP",
};
const clock = new Intl.DateTimeFormat("th-TH", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const dateTime = new Intl.DateTimeFormat("th-TH", { dateStyle: "short", timeStyle: "medium" });
const loginForm = byId("login-form");
const jobForm = byId("registration-form");
const otpForm = byId("otp-form");

let session = null;
let ownJobs = [];
let selectedJobId = null;
let registrationKey = null;
let monitorFilter = "all";
let pendingRead = null;

function currentRoute() {
    return location.pathname === routes.monitor ? "monitor" : "registration";
}

function notice(id, text = "", error = false) {
    const element = byId(id);
    element.textContent = text;
    element.hidden = !text;
    element.classList.toggle("error", error);
}

function showPage() {
    const route = currentRoute();
    byId("login-page").hidden = !!session;
    byId("menu").hidden = !session;
    byId("identity").hidden = !session;
    byId("monitor-link").hidden = session?.role !== "admin";
    byId("username").textContent = session?.username || "";
    byId("role").textContent = session ? (session.role === "admin" ? "แอดมิน" : "ผู้ใช้") : "";
    byId("registration-page").hidden = !session || route !== "registration";
    byId("monitor-page").hidden = session?.role !== "admin" || route !== "monitor";
    for (const link of document.querySelectorAll("a[data-route]")) {
        if (link.dataset.route === route) link.setAttribute("aria-current", "page");
        else link.removeAttribute("aria-current");
    }
}

function openRoute(route, updateHistory = false) {
    const denied = route === "monitor" && session && session.role !== "admin";
    if (denied) history.replaceState({}, "", routes.registration);
    else if (updateHistory && currentRoute() !== route) history.pushState({}, "", routes[route]);
    notice("app-error", denied ? "หน้า Monitor สำหรับแอดมินเท่านั้น" : "", denied);
    pendingRead?.abort();
    showPage();
    if (updateHistory) byId(currentRoute() + "-heading").focus();
    void refresh();
}

function clearSession(message = "") {
    pendingRead?.abort();
    session = null;
    ownJobs = [];
    selectedJobId = null;
    registrationKey = null;
    loginForm.reset();
    jobForm.reset();
    otpForm.reset();
    for (const id of ["app-error", "registration-message", "otp-message"]) notice(id);
    for (const id of ["registration-sync", "monitor-sync"]) byId(id).textContent = "";
    byId("monitor-rows").replaceChildren();
    byId("monitor-empty").hidden = false;
    renderOwnJobs();
    showPage();
    notice("login-error", message, !!message);
}

async function api(path, { body, signal, headers, method = body ? "POST" : "GET" } = {}) {
    const token = session?.accessToken;
    const requestHeaders = new Headers(headers);
    if (token) requestHeaders.set("Authorization", "Bearer " + token);
    if (body) requestHeaders.set("Content-Type", "application/json");
    const timeout = AbortSignal.timeout(15000);
    const response = await fetch(path, {
        method,
        headers: requestHeaders,
        body: body ? JSON.stringify(body) : undefined,
        signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    });
    const data = response.headers.get("Content-Type")?.includes("json") ? await response.json() : null;
    if (!response.ok) {
        if (response.status === 401 && token && session?.accessToken === token) {
            clearSession("เซสชันหมดอายุ กรุณาเข้าสู่ระบบอีกครั้ง");
        }
        const message = errorLabels[data?.code] || Object.values(data?.errors || {}).flat()[0] ||
            data?.title || data?.code || "คำขอไม่สำเร็จ (HTTP " + response.status + ")";
        throw new Error(message);
    }
    return data;
}

function bindForm(form, submit) {
    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        const button = form.querySelector("button[type=submit]");
        if (button.disabled) return;
        button.disabled = true;
        try {
            await submit(Object.fromEntries(new FormData(form)));
        } finally {
            button.disabled = false;
        }
    });
}

function reportError(target, error) {
    const text = error.name === "TimeoutError" || error instanceof TypeError
        ? "ติดต่อ API ไม่สำเร็จ กรุณาลองอีกครั้ง"
        : error.message;
    notice(target, text, true);
}

bindForm(loginForm, async (body) => {
    notice("login-error");
    try {
        session = await api("/v1/auth/login", { body });
        loginForm.reset();
        openRoute(currentRoute());
        byId(currentRoute() + "-heading").focus();
    } catch (error) {
        reportError("login-error", error);
    }
});

byId("logout").addEventListener("click", async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
        await api("/v1/auth/logout", { method: "POST" });
        clearSession();
    } catch (error) {
        if (session) reportError("app-error", error);
    } finally {
        button.disabled = false;
    }
});

byId("menu").addEventListener("click", (event) => {
    const link = event.target.closest("a[data-route]");
    if (!link || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    openRoute(link.dataset.route, true);
});
window.addEventListener("popstate", () => openRoute(currentRoute()));

jobForm.addEventListener("input", () => { registrationKey = null; });
bindForm(jobForm, async (body) => {
    notice("registration-message");
    const token = session?.accessToken;
    const key = registrationKey ??= crypto.randomUUID();
    try {
        const job = await api("/v1/jobs/registration", { body, headers: { "Idempotency-Key": key } });
        if (session?.accessToken !== token) return;
        if (registrationKey === key) {
            registrationKey = null;
            jobForm.reset();
        }
        selectJob(job.jobId);
        notice("registration-message", "รับงานเข้าคิวแล้ว");
    } catch (error) {
        if (session?.accessToken === token) reportError("registration-message", error);
    }
});

bindForm(otpForm, async (body) => {
    const id = selectedJobId;
    const token = session?.accessToken;
    notice("otp-message");
    try {
        await api("/v1/jobs/" + encodeURIComponent(id) + "/otp", { body });
        if (selectedJobId === id && session?.accessToken === token) {
            otpForm.reset();
            notice("otp-message", "ส่ง OTP แล้ว");
        }
    } catch (error) {
        if (selectedJobId === id && session?.accessToken === token) reportError("otp-message", error);
    } finally {
        void refresh();
    }
});

function cell(tr, value, className = "") {
    const td = tr.insertCell();
    if (value instanceof Node) td.append(value);
    else td.textContent = value;
    td.className = className;
    return td;
}

function badge(state) {
    const span = document.createElement("span");
    span.className = "badge";
    span.dataset.state = state;
    span.textContent = stateLabels[state] || state;
    return span;
}

function showOptionalValue(id, value) {
    byId(id + "-row").hidden = !value;
    byId(id).textContent = value || "";
}

function renderJobDetail(job) {
    byId("job-detail").hidden = !job;
    byId("job-empty").hidden = !!job;
    byId("job-empty").textContent = selectedJobId ? "กำลังโหลดสถานะงาน…" : "ส่งงานสมัครหรือเลือกงานจากรายการด้านล่าง";
    if (!job) {
        otpForm.hidden = true;
        return;
    }
    byId("job-state").replaceChildren(badge(job.state));
    byId("job-stage").textContent = stageLabels[job.stage] || job.stage;
    byId("job-id").textContent = job.id;
    showOptionalValue("job-expiry", job.otpExpiresAt ? dateTime.format(new Date(job.otpExpiresAt)) : "");
    showOptionalValue("job-account", job.result?.accountId);
    showOptionalValue("job-error", job.error?.code);
    otpForm.hidden = job.state !== "awaitingOtp";
}

function selectJob(id) {
    if (selectedJobId !== id) {
        selectedJobId = id;
        otpForm.reset();
        notice("otp-message");
    }
    renderOwnJobs();
    void refresh();
}

function renderOwnJobs() {
    renderJobDetail(ownJobs.find((job) => job.id === selectedJobId));
    const rows = byId("history-rows");
    const focusJob = rows.contains(document.activeElement) ? document.activeElement.dataset.job : null;
    rows.replaceChildren();
    for (const job of ownJobs) {
        const tr = rows.insertRow();
        const button = document.createElement("button");
        button.type = "button";
        button.className = "job-link mono";
        button.dataset.job = job.id;
        button.textContent = job.id.slice(0, 8);
        button.title = job.id;
        button.setAttribute("aria-label", "ดูงาน " + job.id);
        button.setAttribute("aria-current", String(job.id === selectedJobId));
        cell(tr, button);
        cell(tr, badge(job.state));
        cell(tr, stageLabels[job.stage] || job.stage);
        cell(tr, dateTime.format(new Date(job.updatedAt)));
        if (focusJob === job.id) button.focus({ preventScroll: true });
    }
    byId("history-table").hidden = ownJobs.length === 0;
    byId("history-empty").hidden = ownJobs.length > 0;
}

byId("history-rows").addEventListener("click", (event) => {
    const id = event.target.closest("button[data-job]")?.dataset.job;
    if (id) selectJob(id);
});

function renderMonitor(jobs) {
    const rows = byId("monitor-rows");
    rows.replaceChildren();
    for (const job of jobs) {
        const tr = rows.insertRow();
        cell(tr, job.owner);
        cell(tr, kindLabels[job.kind] || job.kind);
        cell(tr, badge(job.state));
        const stage = cell(tr, stageLabels[job.stage] || job.stage);
        if (job.error) {
            const hint = document.createElement("span");
            hint.className = "hint mono";
            hint.textContent = [job.error.code, job.error.operation].filter(Boolean).join(" · ");
            stage.append(hint);
        }
        cell(tr, dateTime.format(new Date(job.createdAt)));
        cell(tr, dateTime.format(new Date(job.updatedAt)));
        cell(tr, job.id.slice(0, 8), "mono").title = job.id;
    }
    byId("monitor-empty").hidden = jobs.length > 0;
}

byId("filter").addEventListener("click", (event) => {
    const button = event.target.closest("button[data-filter]");
    if (!button || button.dataset.filter === monitorFilter) return;
    monitorFilter = button.dataset.filter;
    for (const filter of byId("filter").querySelectorAll("button")) {
        filter.setAttribute("aria-pressed", String(filter === button));
    }
    renderMonitor([]);
    byId("monitor-empty").hidden = true;
    void refresh();
});
byId("refresh").addEventListener("click", () => { void refresh(); });

async function refresh() {
    if (!session) return;
    const route = currentRoute();
    if (route === "monitor" && session.role !== "admin") return;
    pendingRead?.abort();
    const controller = new AbortController();
    pendingRead = controller;
    const { signal } = controller;
    const sync = byId(route + "-sync");
    if (!sync.textContent) sync.textContent = "กำลังโหลด…";

    try {
        if (route === "monitor") {
            const query = monitorFilter === "active" ? "limit=50&active=true" : "limit=50";
            const jobs = await api("/v1/admin/jobs?" + query, { signal });
            if (signal.aborted) return;
            renderMonitor(jobs);
        } else {
            const jobs = (await api("/v1/jobs?limit=50", { signal })).filter((job) => job.kind === "registration");
            if (selectedJobId && !jobs.some((job) => job.id === selectedJobId)) {
                jobs.unshift(await api("/v1/jobs/" + encodeURIComponent(selectedJobId), { signal }));
            }
            if (signal.aborted) return;
            ownJobs = jobs;
            renderOwnJobs();
        }
        sync.textContent = "อัปเดต " + clock.format(new Date());
    } catch (error) {
        if (!signal.aborted) sync.textContent = "โหลดสถานะไม่สำเร็จ · " + error.message;
    } finally {
        if (pendingRead === controller) pendingRead = null;
    }
}

setInterval(() => { if (!document.hidden && !pendingRead && session) void refresh(); }, 4000);
document.addEventListener("visibilitychange", () => {
    if (!document.hidden && !pendingRead && session) void refresh();
});
showPage();

