# Native build for the bsv-poker Win32 + WebView2 desktop host (no Tauri, no Rust).
# Locates the MSVC toolchain via vswhere, enters the x64 developer environment (vcvars64), and
# compiles native\main.c with cl.exe, statically linking the vendored WebView2 loader. Output:
# apps\client-desktop\build\bsv-poker.exe (WINDOWS subsystem — no console for end users).
#
# Run: pwsh native\build-native.ps1   (from apps\client-desktop, or anywhere — paths are anchored).
$ErrorActionPreference = 'Stop'

$here   = Split-Path -Parent $MyInvocation.MyCommand.Path          # ...\apps\client-desktop\native
$appDir = Split-Path -Parent $here                                  # ...\apps\client-desktop
$outDir = Join-Path $appDir 'build'
New-Item -ItemType Directory -Force $outDir | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere not found — install Visual Studio Build Tools (C++)." }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { $vsPath = & $vswhere -latest -products * -property installationPath }
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

$inc   = Join-Path $here 'include'
$lib   = Join-Path $here 'lib\x64\WebView2LoaderStatic.lib'
$exe   = Join-Path $outDir 'bsv-poker.exe'
$test  = Join-Path $outDir 'test-lifecycle.exe'

# Objects land in $outDir (we cd there); sources/include/lib are absolute & quoted. No /Fo (its
# trailing backslash collides with cmd quoting when the path contains spaces).
# The native host: main.c + lifecycle.c -> bsv-poker.exe (WINDOWS subsystem). WebView2LoaderStatic.lib
# pulls in version/shlwapi; CommandLineToArgvW needs shell32; COM needs ole32/oleaut32. /TC = C.
$hostArgs = @(
  '/nologo','/TC','/W3','/O2','/MT','/utf-8','/D_UNICODE','/DUNICODE','/D_CRT_SECURE_NO_WARNINGS',
  "/I`"$inc`"", "/Fe`"$exe`"", "`"$(Join-Path $here 'main.c')`"", "`"$(Join-Path $here 'lifecycle.c')`"",
  '/link','/SUBSYSTEM:WINDOWS',
  "`"$lib`"",'user32.lib','ole32.lib','oleaut32.lib','shlwapi.lib','shell32.lib','advapi32.lib','version.lib','gdi32.lib'
)

# The pure-policy unit test: test-lifecycle.c + lifecycle.c -> test-lifecycle.exe (CONSOLE).
$testArgs = @(
  '/nologo','/TC','/W4','/O2','/MT','/utf-8','/D_CRT_SECURE_NO_WARNINGS',
  "/I`"$inc`"", "/Fe`"$test`"", "`"$(Join-Path $here 'test-lifecycle.c')`"", "`"$(Join-Path $here 'lifecycle.c')`"",
  '/link','/SUBSYSTEM:CONSOLE'
)

Write-Host "=== compiling native host + lifecycle test (cl.exe via vcvars64) ==="
& cmd.exe /c ("call `"$vcvars`" >nul && cd /d `"$outDir`" && cl " + ($hostArgs -join ' '))
if ($LASTEXITCODE -ne 0) { throw "cl.exe (host) failed (exit $LASTEXITCODE)" }
& cmd.exe /c ("call `"$vcvars`" >nul && cd /d `"$outDir`" && cl " + ($testArgs -join ' '))
if ($LASTEXITCODE -ne 0) { throw "cl.exe (test) failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $exe))  { throw "build produced no host exe at $exe" }
if (-not (Test-Path $test)) { throw "build produced no test exe at $test" }
Write-Host "BUILD OK -> $exe ($((Get-Item $exe).Length) bytes); $test ($((Get-Item $test).Length) bytes)"
