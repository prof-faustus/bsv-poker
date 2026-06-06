# Wire format

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

Byte-exact formats. Determinism (P2) and the parser's malleability resistance both depend on these.
Source: `packages/protocol-types/src/serialize.ts`, `packages/tx-builder/src/{wire,parse}.ts`,
`packages/app-services/src/session-auth.ts`.

## Canonical object serialization (`serialize.ts`)

Used wherever bytes must be identical across clients (rulesetHash, branch bindings, state hashes,
commitment preimages):

- Little-endian fixed-width integers: u8 / u16 / u32 / u64. **Amounts are u64** (may exceed 2^53, so
  encoded via BigInt). No floats anywhere.
- Enums → a 1-byte ordinal of their declared order.
- Variable-length fields → u32 byte-length prefix.
- Booleans → u8 ∈ {0,1}. Optional fields → a u8 presence flag then the value.
- Hashing: `H = SHA-256` over the canonical bytes (txids use double-SHA-256).
- **Hex decoding is strict** (`hexToBytes`): every nibble validated; malformed hex is rejected, never
  truncated to `0x00`.

## Transaction wire format (`wire.ts` encode / `parse.ts` decode)

```
version  : u32 LE
vin      : CompactSize count, then per input:
             prevTxid  : 32 bytes (LE on wire; big-endian display hex in ParsedTx)
             vout      : u32 LE
             scriptSig : CompactSize length + bytes
             sequence  : u32 LE
vout     : CompactSize count, then per output:
             value     : u64 LE  (satoshis; bigint in ParsedTx — never truncated)
             script    : CompactSize length + bytes
nLockTime: u32 LE
```

**Canonical-form rules enforced by the parser** (`parseTxWire`): CompactSize must be MINIMAL; there
must be **no trailing bytes**; every length is bounded by the remaining buffer. These make the
encoding non-malleable (two byte strings cannot mean the same transaction). `serializeParsedTx` is the
exact inverse on canonical input — the round-trip identity is fuzz-tested (`INV-TXP-F1/F2`).

### CompactSize

`< 0xfd` → 1 byte · `0xfd` → u16 (value ≥ 0xfd) · `0xfe` → u32 (value > 0xffff) · `0xff` → u64
(value > 0xffffffff). Non-minimal encodings are rejected (`INV-TXP-N7`).

## Envelope wire (signed protocol messages)

JSON on the relay; the **signed** canonical form is the array in `PROTOCOL.md`
(`envelopeMessage`). The Go indexer reconstructs it byte-for-byte (HTML escaping disabled, no trailing
newline) — pinned by `INV-IXV-0`.

## Capability token wire

`base64url(payload) "." base64url(HMAC-SHA256(secret, payload))`, `payload = "v1|tableId|scope|exp"`
(`relay-go/capability.go`).

## Why byte-exactness matters

Determinism is the cross-client agreement guarantee (P2): two honest clients hashing the same logical
state must get the same bytes, or they fork. The strict hex codec, minimal CompactSize, and no-trailing
rules are the parts of that guarantee that an attacker would otherwise exploit for malleability.
