/*
 * bsv-poker — native Windows desktop host (Win32 + WebView2). NO Tauri, NO Rust, NO framework.
 * ============================================================================================
 *
 * WHAT: a true native Win32 application (wWinMain + message loop + native top-level window) that
 *   (1) hosts the Microsoft Edge WebView2 control and renders the SAME audited web client the
 *       browser uses — the in-tree-built ES-module bundle at apps/client-web/dist/esm — served to
 *       the webview from the local folder via SetVirtualHostNameToFolderMapping (no HTTP server,
 *       no network, no file:// quirks); and
 *   (2) supervises the local Go services (relay-go, indexer-go): it launches them under a Win32 Job
 *       Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so they are terminated deterministically when
 *       the app exits — no orphaned/zombie processes (host rule). Ordered start, reverse-order stop.
 *
 * WHY native Win32 + WebView2 (not Tauri): Tauri is a large external framework (Rust + a crate graph)
 *   whose internals an auditor cannot fully see, and it could not even be compiled in this
 *   environment. WebView2 is the Microsoft-native embedding component (an OS runtime, like the Win32
 *   API itself); the host is a single, fully-visible C translation unit over the Windows SDK + the
 *   WebView2 ABI header. There is exactly ONE audited client core (the TypeScript engine running in
 *   the webview) — the desktop shell does not re-implement or fork it.
 *
 * WHY WebView2.h + WebView2LoaderStatic.lib are vendored under native/: they are the Microsoft ABI
 *   contract + bootstrap loader for the OS WebView2 runtime — the same category as windows.h /
 *   kernel32. The loader is linked STATICALLY, so the shipped exe carries no extra DLL. Provenance
 *   and version are recorded in native/THIRD-PARTY.md.
 *
 * HOW (COM in C): WebView2 is a COM API. Compiled as C with COBJMACROS, WebView2.h exposes the
 *   vtable structs and ICoreWebView2_*_Method(This, ...) call macros; the interface IIDs are defined
 *   inline in the header (__declspec(selectany)). The four asynchronous completion/event callbacks
 *   are COM objects we implement here as static singletons (QueryInterface/AddRef/Release/Invoke).
 *
 * SELF-TEST (--selftest --out FILE [--content DIR]): proves the host RENDERS, headlessly and
 *   automatably. It creates the window hidden, initialises WebView2, maps + navigates to the local
 *   client, waits for NavigationCompleted, runs `document.getElementById('root').innerText` via
 *   ExecuteScript, writes the (JSON-encoded) result to FILE, and exits 0. A watchdog timer fails the
 *   run (exit 2) if navigation never completes. verify-desktop.ts asserts the lobby text is present —
 *   the same render bar the web client meets, enforced for the native shell too.
 *
 * Build: native/build-native.ps1 (cl.exe, /TC). Subsystem: WINDOWS (no console window for users);
 *   the self-test writes its result to a file, so it needs no console.
 */

#define COBJMACROS
#define CINTERFACE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <strsafe.h>
#include <stdio.h>
#include "WebView2.h"
#include "lifecycle.h"

/* ----------------------------------------------------------------------------------------------
 * Global host state. Single-window, single-webview app; a flat struct keeps the COM callbacks (which
 * receive no user context pointer in this minimal binding) able to reach what they need.
 * -------------------------------------------------------------------------------------------- */
static struct {
    int            selftest;        /* 1 in --selftest mode (hidden window, script-extract, exit). */
    wchar_t        outfile[MAX_PATH];   /* selftest: where the extracted #root text is written. */
    wchar_t        content[MAX_PATH];   /* absolute folder mapped into the webview as the site root. */
    HWND           hwnd;
    HANDLE         job;             /* Job Object owning the supervised service processes. */
    HANDLE         svc[4];          /* supervised child handles, in START order (for reverse stop). */
    int            svcCount;
    ICoreWebView2Controller* controller;
    ICoreWebView2* webview;
    int            exitCode;
    int            navDone;
} g;

static const wchar_t* HOST_NAME = L"bsvpoker.local";
static const wchar_t* START_URL = L"https://bsvpoker.local/index.html";

/* ----------------------------------------------------------------------------------------------
 * COM callback singletons. Each implements its WebView2 callback interface. Lifetime is the whole
 * process, so AddRef/Release are no-ops returning 1 (WebView2 holds a reference but never frees a
 * static); QueryInterface answers for IUnknown and the object's own interface.
 * -------------------------------------------------------------------------------------------- */
#define QI_BODY(OWNIID)                                                                            \
    if (!ppv) return E_POINTER;                                                                    \
    if (IsEqualIID(riid, &IID_IUnknown) || IsEqualIID(riid, &(OWNIID))) { *ppv = This; return S_OK; } \
    *ppv = NULL; return E_NOINTERFACE;

static void post_quit(int code) { g.exitCode = code; PostQuitMessage(code); }

/* --- ICoreWebView2ExecuteScriptCompletedHandler: receives the JSON result of ExecuteScript. --- */
static HRESULT STDMETHODCALLTYPE Exec_QI(ICoreWebView2ExecuteScriptCompletedHandler* This, REFIID riid, void** ppv)
    { QI_BODY(IID_ICoreWebView2ExecuteScriptCompletedHandler) }
static ULONG STDMETHODCALLTYPE Exec_AddRef(ICoreWebView2ExecuteScriptCompletedHandler* This) { (void)This; return 1; }
static ULONG STDMETHODCALLTYPE Exec_Release(ICoreWebView2ExecuteScriptCompletedHandler* This) { (void)This; return 1; }
static HRESULT STDMETHODCALLTYPE Exec_Invoke(ICoreWebView2ExecuteScriptCompletedHandler* This, HRESULT ec, LPCWSTR json) {
    (void)This;
    if (FAILED(ec) || !json) { post_quit(3); return S_OK; }
    HANDLE f = CreateFileW(g.outfile, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE) {
        int bytes = WideCharToMultiByte(CP_UTF8, 0, json, -1, NULL, 0, NULL, NULL);
        if (bytes > 1) {
            char* utf8 = (char*)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)bytes);
            if (utf8) {
                WideCharToMultiByte(CP_UTF8, 0, json, -1, utf8, bytes, NULL, NULL);
                DWORD wrote; WriteFile(f, utf8, (DWORD)(bytes - 1), &wrote, NULL);
                HeapFree(GetProcessHeap(), 0, utf8);
            }
        }
        CloseHandle(f);
    }
    post_quit(0);
    return S_OK;
}
static ICoreWebView2ExecuteScriptCompletedHandlerVtbl g_execVtbl = { Exec_QI, Exec_AddRef, Exec_Release, Exec_Invoke };
static ICoreWebView2ExecuteScriptCompletedHandler g_exec = { &g_execVtbl };

/* --- ICoreWebView2NavigationCompletedEventHandler: fires when the page finishes loading. --- */
static HRESULT STDMETHODCALLTYPE Nav_QI(ICoreWebView2NavigationCompletedEventHandler* This, REFIID riid, void** ppv)
    { QI_BODY(IID_ICoreWebView2NavigationCompletedEventHandler) }
static ULONG STDMETHODCALLTYPE Nav_AddRef(ICoreWebView2NavigationCompletedEventHandler* This) { (void)This; return 1; }
static ULONG STDMETHODCALLTYPE Nav_Release(ICoreWebView2NavigationCompletedEventHandler* This) { (void)This; return 1; }
static HRESULT STDMETHODCALLTYPE Nav_Invoke(ICoreWebView2NavigationCompletedEventHandler* This,
                                            ICoreWebView2* sender, ICoreWebView2NavigationCompletedEventArgs* args) {
    (void)This; (void)args;
    g.navDone = 1;
    if (g.selftest) {
        /* Read the mounted lobby's text back out of the live DOM — proof the client rendered inside
         * the native host (not merely that a window exists). */
        ICoreWebView2_ExecuteScript(sender,
            L"(document.getElementById('root')&&document.getElementById('root').innerText)||''", &g_exec);
    }
    return S_OK;
}
static ICoreWebView2NavigationCompletedEventHandlerVtbl g_navVtbl = { Nav_QI, Nav_AddRef, Nav_Release, Nav_Invoke };
static ICoreWebView2NavigationCompletedEventHandler g_nav = { &g_navVtbl };

/* --- ICoreWebView2CreateCoreWebView2ControllerCompletedHandler: webview controller is ready. --- */
static HRESULT STDMETHODCALLTYPE Ctl_QI(ICoreWebView2CreateCoreWebView2ControllerCompletedHandler* This, REFIID riid, void** ppv)
    { QI_BODY(IID_ICoreWebView2CreateCoreWebView2ControllerCompletedHandler) }
static ULONG STDMETHODCALLTYPE Ctl_AddRef(ICoreWebView2CreateCoreWebView2ControllerCompletedHandler* This) { (void)This; return 1; }
static ULONG STDMETHODCALLTYPE Ctl_Release(ICoreWebView2CreateCoreWebView2ControllerCompletedHandler* This) { (void)This; return 1; }
static HRESULT STDMETHODCALLTYPE Ctl_Invoke(ICoreWebView2CreateCoreWebView2ControllerCompletedHandler* This,
                                            HRESULT ec, ICoreWebView2Controller* controller) {
    (void)This;
    if (FAILED(ec) || !controller) { post_quit(4); return S_OK; }
    g.controller = controller;
    ICoreWebView2Controller_AddRef(controller);

    RECT rc; GetClientRect(g.hwnd, &rc);
    ICoreWebView2Controller_put_Bounds(controller, rc);
    ICoreWebView2Controller_put_IsVisible(controller, TRUE);

    if (FAILED(ICoreWebView2Controller_get_CoreWebView2(controller, &g.webview)) || !g.webview) { post_quit(5); return S_OK; }

    /* Map the local built bundle to https://bsvpoker.local/ (needs ICoreWebView2_3). DENY_CORS keeps
     * the mapped origin from being reachable cross-origin by other pages. */
    ICoreWebView2_3* wv3 = NULL;
    if (SUCCEEDED(ICoreWebView2_QueryInterface(g.webview, &IID_ICoreWebView2_3, (void**)&wv3)) && wv3) {
        ICoreWebView2_3_SetVirtualHostNameToFolderMapping(wv3, HOST_NAME, g.content,
            COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
        ICoreWebView2_3_Release(wv3);
    } else {
        post_quit(6); return S_OK;
    }

    /* Expose the runtime port map to the UI before any page script runs (REQ-APP-027 — the UI reads
     * ports from runtime config, they are not hard-coded in the client). regtest-only by default
     * (REQ-APP-029/030); the mainnet switch policy is enforced + tested in lifecycle.c. */
    unsigned rp, ip, np; bsv_runtime_ports(&rp, &ip, &np);
    wchar_t inject[256];
    StringCchPrintfW(inject, 256,
        L"window.__BSV_RUNTIME={ports:{relay:%u,indexer:%u,node:%u},network:'regtest'};", rp, ip, np);
    ICoreWebView2_AddScriptToExecuteOnDocumentCreated(g.webview, inject, NULL);

    EventRegistrationToken tok;
    ICoreWebView2_add_NavigationCompleted(g.webview, &g_nav, &tok);
    ICoreWebView2_Navigate(g.webview, START_URL);
    return S_OK;
}
static ICoreWebView2CreateCoreWebView2ControllerCompletedHandlerVtbl g_ctlVtbl = { Ctl_QI, Ctl_AddRef, Ctl_Release, Ctl_Invoke };
static ICoreWebView2CreateCoreWebView2ControllerCompletedHandler g_ctl = { &g_ctlVtbl };

/* --- ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler: WebView2 env is ready. --- */
static HRESULT STDMETHODCALLTYPE Env_QI(ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler* This, REFIID riid, void** ppv)
    { QI_BODY(IID_ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler) }
static ULONG STDMETHODCALLTYPE Env_AddRef(ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler* This) { (void)This; return 1; }
static ULONG STDMETHODCALLTYPE Env_Release(ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler* This) { (void)This; return 1; }
static HRESULT STDMETHODCALLTYPE Env_Invoke(ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler* This,
                                            HRESULT ec, ICoreWebView2Environment* env) {
    (void)This;
    if (FAILED(ec) || !env) { post_quit(7); return S_OK; }
    ICoreWebView2Environment_CreateCoreWebView2Controller(env, g.hwnd, &g_ctl);
    return S_OK;
}
static ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandlerVtbl g_envVtbl = { Env_QI, Env_AddRef, Env_Release, Env_Invoke };
static ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler g_env = { &g_envVtbl };

/* ----------------------------------------------------------------------------------------------
 * Service supervision under a kill-on-close Job Object. Ordered startup with the BOUNDED restart
 * policy (REQ-APP-021/022) using the pure lifecycle policy (lifecycle.c); child handles are recorded
 * in start order so shutdown can terminate them in REVERSE order (REQ-APP-021) before the Job closes.
 * `-addr <host:port>` mirrors the previous supervisor's service contract (REQ-APP-027).
 * -------------------------------------------------------------------------------------------- */

/* Spawn one bundled service; on success record + job-assign its handle and return 1. Missing binary
 * (dev) is not a failure to retry — returns 1 so the bounded loop does not spin on an absent file. */
static int spawn_service(const wchar_t* exeDir, const wchar_t* exe, const wchar_t* addr) {
    wchar_t path[MAX_PATH];
    StringCchPrintfW(path, MAX_PATH, L"%s\\%s", exeDir, exe);
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return 1; /* optional in dev. */
    STARTUPINFOW si; ZeroMemory(&si, sizeof si); si.cb = sizeof si;
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof pi);
    wchar_t cmd[MAX_PATH * 2];
    StringCchPrintfW(cmd, MAX_PATH * 2, L"\"%s\" -addr %s", path, addr);
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) return 0;
    if (g.job) AssignProcessToJobObject(g.job, pi.hProcess);
    CloseHandle(pi.hThread);
    if (g.svcCount < (int)(sizeof g.svc / sizeof g.svc[0])) g.svc[g.svcCount++] = pi.hProcess;
    else CloseHandle(pi.hProcess);
    return 1;
}

/* Bounded retry wrapper (REQ-APP-022): try to start `exe` up to BSV_MAX_RESTARTS times. */
static void start_one(const wchar_t* exeDir, const wchar_t* exe, const wchar_t* addr) {
    for (unsigned a = 0; bsv_should_retry(a, BSV_MAX_RESTARTS); ++a) {
        if (spawn_service(exeDir, exe, addr)) return;
        Sleep((DWORD)bsv_backoff_ms(a));
    }
}

static void start_services(const wchar_t* exeDir) {
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli; ZeroMemory(&jeli, sizeof jeli);
    jeli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    g.job = CreateJobObjectW(NULL, NULL);
    if (g.job) SetInformationJobObject(g.job, JobObjectExtendedLimitInformation, &jeli, sizeof jeli);
    /* Ordered startup (node is the embedded in-tree adapter; indexer then relay are the Go services).
     * The order is the canonical bsv_startup_order(); we spawn the two that have binaries here. */
    start_one(exeDir, L"indexer-go.exe", L"127.0.0.1:8092");
    start_one(exeDir, L"relay-go.exe",   L"127.0.0.1:8091");
}

/* Reverse-order shutdown (REQ-APP-021): terminate children newest-first, then close the Job (whose
 * KILL_ON_JOB_CLOSE limit is the backstop for anything still alive). */
static void stop_services(void) {
    for (int i = g.svcCount - 1; i >= 0; --i) {
        if (g.svc[i]) { TerminateProcess(g.svc[i], 0); CloseHandle(g.svc[i]); g.svc[i] = NULL; }
    }
    g.svcCount = 0;
    if (g.job) { CloseHandle(g.job); g.job = NULL; }
}

/* ----------------------------------------------------------------------------------------------
 * Window + entry.
 * -------------------------------------------------------------------------------------------- */
static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_SIZE:
            if (g.controller) {
                RECT rc; GetClientRect(hwnd, &rc);
                ICoreWebView2Controller_put_Bounds(g.controller, rc);
            }
            return 0;
        case WM_DESTROY:
            post_quit(g.exitCode);
            return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

/* Resolve the content folder: --content if given, else "<exeDir>\web", else the dev build path
 * "<exeDir>\..\..\client-web\dist\esm". Result is an absolute path. */
static void resolve_content(const wchar_t* exeDir, const wchar_t* override) {
    wchar_t cand[MAX_PATH];
    if (override && override[0]) { GetFullPathNameW(override, MAX_PATH, g.content, NULL); return; }
    StringCchPrintfW(cand, MAX_PATH, L"%s\\web", exeDir);
    if (GetFileAttributesW(cand) != INVALID_FILE_ATTRIBUTES) { GetFullPathNameW(cand, MAX_PATH, g.content, NULL); return; }
    StringCchPrintfW(cand, MAX_PATH, L"%s\\..\\..\\client-web\\dist\\esm", exeDir);
    GetFullPathNameW(cand, MAX_PATH, g.content, NULL);
}

static const wchar_t* arg_value(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 1; i + 1 < argc; ++i) if (lstrcmpiW(argv[i], name) == 0) return argv[i + 1];
    return NULL;
}
static int arg_flag(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 1; i < argc; ++i) if (lstrcmpiW(argv[i], name) == 0) return 1;
    return 0;
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, PWSTR cmdline, int nShow) {
    (void)hPrev; (void)cmdline;
    int argc = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    g.selftest = arg_flag(argc, argv, L"--selftest");
    const wchar_t* out = arg_value(argc, argv, L"--out");
    if (out) StringCchCopyW(g.outfile, MAX_PATH, out);

    wchar_t exePath[MAX_PATH]; GetModuleFileNameW(NULL, exePath, MAX_PATH);
    wchar_t exeDir[MAX_PATH]; StringCchCopyW(exeDir, MAX_PATH, exePath);
    PathRemoveFileSpecW(exeDir);
    resolve_content(exeDir, arg_value(argc, argv, L"--content"));

    if (g.selftest && !g.outfile[0]) return 64; /* selftest requires --out */

    HRESULT hrco = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    if (FAILED(hrco)) return 70;

    WNDCLASSEXW wc; ZeroMemory(&wc, sizeof wc); wc.cbSize = sizeof wc;
    wc.lpfnWndProc = WndProc; wc.hInstance = hInst; wc.lpszClassName = L"BsvPokerHost";
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    RegisterClassExW(&wc);

    g.hwnd = CreateWindowExW(0, L"BsvPokerHost", L"bsv-poker (REGTEST)",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1200, 800, NULL, NULL, hInst, NULL);
    if (!g.hwnd) { CoUninitialize(); return 71; }

    if (!g.selftest) {
        start_services(exeDir);            /* user run: bring up the local services. */
        ShowWindow(g.hwnd, nShow ? nShow : SW_SHOW);
        UpdateWindow(g.hwnd);
    } else {
        /* Hidden window is fine — WebView2 renders and ExecuteScript works without showing it.
         * Watchdog: if navigation never completes, fail the self-test instead of hanging. */
        SetTimer(g.hwnd, 1, 30000, NULL);
    }

    /* User-data folder for the WebView2 profile (writable, per-user). */
    wchar_t udf[MAX_PATH];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", udf, MAX_PATH)) StringCchCopyW(udf, MAX_PATH, exeDir);
    StringCchCatW(udf, MAX_PATH, L"\\bsv-poker\\webview2");

    HRESULT hr = CreateCoreWebView2EnvironmentWithOptions(NULL, udf, NULL, &g_env);
    if (FAILED(hr)) { if (g.job) CloseHandle(g.job); CoUninitialize(); return 72; }

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0)) {
        if (g.selftest && msg.message == WM_TIMER && msg.hwnd == g.hwnd && !g.navDone) { g.exitCode = 2; break; }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (g.webview) ICoreWebView2_Release(g.webview);
    if (g.controller) { ICoreWebView2Controller_Close(g.controller); ICoreWebView2Controller_Release(g.controller); }
    stop_services();   /* reverse-order terminate, then close the Job (KILL_ON_JOB_CLOSE backstop). */
    CoUninitialize();
    LocalFree(argv);
    return g.exitCode;
}
