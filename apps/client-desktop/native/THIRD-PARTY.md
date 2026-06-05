# Vendored third-party — native desktop host

The native Win32 host links the Microsoft Edge **WebView2** OS component. To build offline/standalone
(no build-time package fetch), the WebView2 **ABI header** and the **static loader** are vendored
here. These are the interface contract + bootstrap for an operating-system component — the same
category as the Windows SDK headers (`windows.h`) and import libraries we already compile against.
They are NOT the audited application logic.

| File | Source | Version | Purpose |
|------|--------|---------|---------|
| `include/WebView2.h` | NuGet `Microsoft.Web.WebView2` | 1.0.3967.48 | COM ABI for the WebView2 runtime (interfaces + inline IIDs). |
| `include/WebView2EnvironmentOptions.h` | NuGet `Microsoft.Web.WebView2` | 1.0.3967.48 | Environment-options helper declarations. |
| `lib/x64/WebView2LoaderStatic.lib` | NuGet `Microsoft.Web.WebView2` | 1.0.3967.48 | Static loader: locates + bootstraps the installed WebView2 runtime. Linked statically so the exe ships no extra DLL. |

Provenance: `https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3967.48/microsoft.web.webview2.1.0.3967.48.nupkg`
(a zip; the files above are extracted from `build/native/`). License: the Microsoft Web Components
license bundled in that package (`LICENSE.txt`). The WebView2 **runtime** itself is an OS component
distributed by Microsoft (present on Windows 11; otherwise the Evergreen Runtime).

To refresh: download the same NuGet package, replace the three files, and re-run
`native/build-native.ps1`. The host calls only documented, stable WebView2 interfaces
(`ICoreWebView2`, `ICoreWebView2_3`, `ICoreWebView2Controller`, `ICoreWebView2Environment` and the
four completion/event handlers), so a version bump is drop-in.
