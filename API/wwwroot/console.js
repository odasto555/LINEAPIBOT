"use strict";

const byId = (id) => document.getElementById(id);
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
const clock = new Intl.DateTimeFormat("th-TH", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const dateTime = new Intl.DateTimeFormat("th-TH", { dateStyle: "short", timeStyle: "medium" });
const loginForm = byId("login-form");
const syncLabel = byId("monitor-sync");
let session = null;
let activeOnly = false;
let pendingRead = null;

function notice(id, text = "") {
    const element = byId(id);
    element.textContent = text;
    element.hidden = !text;
}

function showSession(next) {
    pendingRead?.abort();
    session = next;
    byId("login-page").hidden = !!session;
    byId("identity").hidden = !session;
    byId("monitor-page").hidden = !session;
    byId("username").textContent = session?.username || "";
    byId("role").textContent = session ? "แอดมิน" : "";
    notice("app-error");
    syncLabel.textContent = "";
    loginForm.reset();
    renderJobs([]);
}

async function api(path, { body, signal, token = session?.accessToken, method = body ? "POST" : "GET" } = {}) {
    const headers = new Headers();
    if (token) headers.set("Authorization", "Bearer " + token);
    if (body) headers.set("Content-Type", "application/json");
    const timeout = AbortSignal.timeout(15000);
    const response = await fetch(path, {
        method,
        headers,
        body: body ? JSON.stringify(body) : undefined,
        signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    });
    if (!response.ok) {
        if (response.status === 401 && token && session?.accessToken === token) {
            showSession(null);
            notice("login-error", "เซสชันหมดอายุ กรุณาเข้าสู่ระบบอีกครั้ง");
        }
        if (path.endsWith("/login") && response.status === 401) {
            throw new Error("ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง");
        }
        throw new Error("คำขอไม่สำเร็จ (HTTP " + response.status + ")");
    }
    return response.status === 204 ? null : response.json();
}

loginForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = loginForm.querySelector("button[type=submit]");
    if (button.disabled) return;
    button.disabled = true;
    notice("login-error");
    try {
        const login = await api("/v1/auth/login", { body: Object.fromEntries(new FormData(loginForm)) });
        if (login.role !== "admin") {
            loginForm.elements.password.value = "";
            notice("login-error", "หน้า Monitor สำหรับแอดมินเท่านั้น");
            await api("/v1/auth/logout", { method: "POST", token: login.accessToken }).catch(() => {});
            return;
        }
        showSession(login);
        byId("monitor-heading").focus();
        void refresh();
    } catch (error) {
        notice("login-error", error.message);
    } finally {
        button.disabled = false;
    }
});

byId("logout").addEventListener("click", async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
        await api("/v1/auth/logout", { method: "POST" });
        showSession(null);
    } catch (error) {
        if (session) notice("app-error", error.message);
    } finally {
        button.disabled = false;
    }
});

function cell(tr, text, className = "") {
    const td = tr.insertCell();
    td.textContent = text;
    td.className = className;
    return td;
}

function renderJobs(jobs) {
    const rows = byId("monitor-rows");
    rows.replaceChildren();
    for (const job of jobs) {
        const tr = rows.insertRow();
        cell(tr, job.owner);
        cell(tr, kindLabels[job.kind] || job.kind);
        const state = document.createElement("span");
        state.className = "badge";
        state.dataset.state = job.state;
        state.textContent = stateLabels[job.state] || job.state;
        tr.insertCell().append(state);
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
    if (!button || (button.dataset.filter === "active") === activeOnly) return;
    activeOnly = button.dataset.filter === "active";
    for (const filter of byId("filter").querySelectorAll("button")) {
        filter.setAttribute("aria-pressed", String(filter === button));
    }
    renderJobs([]);
    byId("monitor-empty").hidden = true;
    void refresh();
});
byId("refresh").addEventListener("click", () => { void refresh(); });

async function refresh() {
    if (!session) return;

    pendingRead?.abort();
    const controller = new AbortController();
    pendingRead = controller;
    const { signal } = controller;
    if (!syncLabel.textContent) syncLabel.textContent = "กำลังโหลด…";

    try {
        const query = activeOnly ? "limit=50&active=true" : "limit=50";
        const jobs = await api("/v1/admin/jobs?" + query, { signal });
        if (!signal.aborted) {
            renderJobs(jobs);
            syncLabel.textContent = "อัปเดต " + clock.format(new Date());
        }
    } catch (error) {
        if (!signal.aborted) syncLabel.textContent = "โหลดสถานะไม่สำเร็จ · " + error.message;
    } finally {
        if (pendingRead === controller) pendingRead = null;
    }
}

setInterval(() => { if (!document.hidden && !pendingRead && session) void refresh(); }, 4000);
document.addEventListener("visibilitychange", () => {
    if (!document.hidden && !pendingRead && session) void refresh();
});

