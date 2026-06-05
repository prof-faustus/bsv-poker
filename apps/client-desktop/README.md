# client-desktop — native Windows desktop host (Win32 + WebView2)

The Windows desktop program (core §11.1, app §A3, AD2): a **true native Win32 application** written
in C against the Windows SDK + the Microsoft Edge **WebView2** ABI. **No Tauri, no Rust, no UI
framework.** It supervises the local Go services and hosts the SAME audited web client the browser
uses, so a non-technical user double-clicks and plays.

There is exactly ONE audited client core (the TypeScript engine, running inside WebView2). The
desktop shell does not re-implement or fork it — it renders the in-tree-built ES-module bundle
(`apps/client-web/dist/esm`) served to the webview from the local folder via
`SetVirtualHostNameToFolderMapping` (no HTTP server, no `file://` quirks, no network for the UI).

## What it does

- **Native window + message loop** (`native/main.c`: `wWinMain`) hosting the WebView2 control.
- **Service supervision** (`native/lifecycle.c` policy + `main.c` wiring): ordered startup
  (node → indexer → relay → settlement, REQ-APP-021) with a **bounded** restart policy
  (`BSV_MAX_RESTARTS`, exponential backoff capped, REQ-APP-022 / NASA Power-of-Ten), reverse-order
  shutdown, and a Win32 **Job Object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so no service is
  orphaned when the app exits.
- **Runtime config to the UI** (REQ-APP-027): injects `window.__BSV_RUNTIME` (ports + network) before
  any page script runs — ports are not hard-coded in the client.
- **Regtest-only by default** (REQ-APP-029/030); the mainnet-switch guard is enforced + unit-tested in
  `native/lifecycle.c` (`bsv_validate_network_switch`).

## Build & verify (Windows; MSVC Build Tools + WebView2 runtime)

```
node ../client-web/build.ts                     # build the web bundle the host renders
pwsh native/build-native.ps1                    # cl.exe -> build/bsv-poker.exe + build/test-lifecycle.exe
build/test-lifecycle.exe                         # pure lifecycle-policy unit tests (exit 0 = pass)
node verify-desktop.ts                           # headless render proof: WebView2 mounts the lobby
build/bsv-poker.exe                              # run it
```

The toolchain is the **MSVC Build Tools** (`cl.exe`, located via `vswhere`) and the **WebView2
runtime** (present on Windows 11). The WebView2 ABI header and the static loader are vendored under
`native/include` and `native/lib` — see [`native/THIRD-PARTY.md`](native/THIRD-PARTY.md) for
provenance and version. The loader is linked statically, so the shipped exe carries no extra DLL.

`verify-desktop.ts` runs the host in `--selftest` mode: it creates the WebView2 hidden, navigates to
the local client, reads `#root.innerText` back out of the live DOM, and asserts the lobby rendered —
the same render bar the web client meets, enforced for the native shell. CI runs the build, the
lifecycle test, and this render proof (skipping only where MSVC / the WebView2 runtime is absent).
