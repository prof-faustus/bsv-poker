# BSV Poker — web version

The web version lives in its **own repository**: **https://github.com/prof-faustus/bsv-poker-web**

It runs the **same protocol as this desktop app**, compiled to **WebAssembly** (Blazor). It does not reimplement
anything — the web repo pulls *this* repo in as a git **submodule** (`core/`) and references the identical
`BsvPoker.Crypto`, `BsvPoker.Core`, and `BsvPoker.Net` engines, compiled to WASM. Only the transport differs.

To make that possible, the game engines were decoupled from the TCP `P2PNode` onto the existing `IGameTransport`
interface (here in `BsvPoker.Net`), so the same engine runs over the desktop mesh **or** a browser transport with no
change. The browser's first transport is `BroadcastChannelTransport` — server-less local P2P that connects every open
tab on the origin. Cross-machine play will add a WebRTC transport (same interface).

## Run it
```bash
git clone --recursive https://github.com/prof-faustus/bsv-poker-web.git
cd bsv-poker-web/src/BsvPoker.Web
dotnet run            # then open http://localhost:5099 in two tabs and Join in both
```

## Roadmap
1. Browser BSV wallet → the real on-chain n-of-n pot funds/settles from the web (miner first-seen verified).
2. WebRTC transport → cross-machine browser play.
3. A paid **dealer node** that keeps tables open.
