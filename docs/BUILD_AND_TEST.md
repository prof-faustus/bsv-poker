# Build, test, and release

## Prerequisites

- **.NET 8 SDK.**
- **Windows** for building/running the app (WPF). The `Crypto`, `Core`, and `Net` libraries and the test
  runner are plain `net8.0` and are cross-platform; only `BsvPoker.App` is `net8.0-windows`.

## Build

```powershell
dotnet build dotnet/BsvPoker.sln -c Release
```

## Test

The test project is a **dependency-free console runner** — no test framework. It returns a non-zero exit
code if any assertion fails, so CI treats a failure as a red build.

```powershell
dotnet run --project dotnet/test/BsvPoker.Tests/BsvPoker.Tests.csproj -c Release
```

Suites cover: secp256k1 (known key vectors + random-nonce sign/verify), the crypto primitives, the game engine
and the six variants, the BSV chain/sighash/recovery, the BSV-native wallet keys, the wallet extras
(WIF, signed messages, seed encryption), the card NFTs, the P2P transport, the networked game, and the
encrypted chat (including history persistence). Each behavioural claim has a positive test and, where it
matters, a hostile-negative test.

## Publish the single-file `poker.exe`

```powershell
dotnet publish dotnet/src/BsvPoker.App/BsvPoker.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
Remove-Item dist/*.pdb -ErrorAction SilentlyContinue
```

`dist/poker.exe` is self-contained: no installer, no separate DLLs, no .NET runtime prerequisite. Run it
by double-clicking. Two copies on one machine are two independent players.

## Continuous integration

`.github/workflows/ci.yml` restores, builds the whole solution in Release, and runs the test suite on
`windows-latest`. `.github/workflows/release-desktop.yml` publishes `poker.exe` and attaches it to the
GitHub Release when a `v*` tag is pushed (and uploads it as an artifact on non-tag runs).
