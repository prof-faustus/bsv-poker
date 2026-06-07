using BsvPoker.Crypto;

namespace BsvPoker.Core;

/// <summary>
/// BSV transactions: model, wire serialization, txid, P2PKH scripts, the BSV FORKID sighash,
/// input signing (secp256k1, low-S, DER + 0x41 hashtype), and a pre-signed nLockTime RECOVERY builder
/// (future locktime + non-final sequence so the locktime binds) — so a player can always reclaim funds.
/// OP_RETURN is never produced. Same model on regtest/testnet/mainnet (network is only an address tag).
/// </summary>
public static class Chain
{
    public sealed record TxIn(string PrevTxid, uint Vout, byte[] ScriptSig, uint Sequence);
    public sealed record TxOut(long Value, byte[] Script);
    public sealed record Tx(uint Version, List<TxIn> Ins, List<TxOut> Outs, uint LockTime);

    private const byte SighashAllForkId = 0x41; // SIGHASH_ALL | SIGHASH_FORKID (BSV)

    // ---- encoding helpers ----
    private static void U32(List<byte> b, uint v) { b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24)); }
    private static void U64(List<byte> b, long v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (8 * i))); }
    private static void VarInt(List<byte> b, long n)
    {
        if (n < 0xfd) b.Add((byte)n);
        else if (n <= 0xffff) { b.Add(0xfd); b.Add((byte)n); b.Add((byte)(n >> 8)); }
        else if (n <= 0xffffffff) { b.Add(0xfe); U32(b, (uint)n); }
        else { b.Add(0xff); U64(b, n); }
    }
    private static byte[] RevHex(string hex) { var a = Convert.FromHexString(hex); Array.Reverse(a); return a; } // display→internal

    public static byte[] Serialize(Tx tx)
    {
        var b = new List<byte>();
        U32(b, tx.Version);
        VarInt(b, tx.Ins.Count);
        foreach (var i in tx.Ins) { b.AddRange(RevHex(i.PrevTxid)); U32(b, i.Vout); VarInt(b, i.ScriptSig.Length); b.AddRange(i.ScriptSig); U32(b, i.Sequence); }
        VarInt(b, tx.Outs.Count);
        foreach (var o in tx.Outs) { U64(b, o.Value); VarInt(b, o.Script.Length); b.AddRange(o.Script); }
        U32(b, tx.LockTime);
        return b.ToArray();
    }

    /// <summary>Display txid (big-endian hex of sha256d of the serialized tx).</summary>
    public static string Txid(Tx tx) { var h = Hashes.Sha256d(Serialize(tx)); Array.Reverse(h); return Convert.ToHexString(h).ToLowerInvariant(); }

    /// <summary>P2PKH locking script: OP_DUP OP_HASH160 &lt;20&gt; OP_EQUALVERIFY OP_CHECKSIG.</summary>
    public static byte[] P2pkhLock(byte[] hash160) { var b = new List<byte> { 0x76, 0xa9, 0x14 }; b.AddRange(hash160); b.Add(0x88); b.Add(0xac); return b.ToArray(); }
    public static byte[] P2pkhLockForPub(byte[] pub33) => P2pkhLock(Hashes.Hash160(pub33));
    private static byte[] Push(byte[] data) { var b = new List<byte> { (byte)data.Length }; b.AddRange(data); return b.ToArray(); } // small pushes only

    /// <summary>The BSV FORKID sighash digest for input <paramref name="index"/> (SIGHASH_ALL|FORKID).</summary>
    public static byte[] SighashForkId(Tx tx, int index, byte[] scriptCode, long amount)
    {
        var prevouts = new List<byte>(); var seqs = new List<byte>(); var outs = new List<byte>();
        foreach (var i in tx.Ins) { prevouts.AddRange(RevHex(i.PrevTxid)); U32(prevouts, i.Vout); U32(seqs, i.Sequence); }
        foreach (var o in tx.Outs) { U64(outs, o.Value); VarInt(outs, o.Script.Length); outs.AddRange(o.Script); }
        var hashPrevouts = Hashes.Sha256d(prevouts.ToArray());
        var hashSequence = Hashes.Sha256d(seqs.ToArray());
        var hashOutputs = Hashes.Sha256d(outs.ToArray());
        var p = new List<byte>();
        U32(p, tx.Version);
        p.AddRange(hashPrevouts);
        p.AddRange(hashSequence);
        var inp = tx.Ins[index];
        p.AddRange(RevHex(inp.PrevTxid)); U32(p, inp.Vout);
        VarInt(p, scriptCode.Length); p.AddRange(scriptCode);
        U64(p, amount);
        U32(p, inp.Sequence);
        p.AddRange(hashOutputs);
        U32(p, tx.LockTime);
        U32(p, SighashAllForkId);
        return Hashes.Sha256d(p.ToArray());
    }

    /// <summary>Sign input <paramref name="index"/> spending a P2PKH output of <paramref name="pub33"/>; sets the scriptSig.</summary>
    public static Tx SignP2pkhInput(Tx tx, int index, byte[] privSeed, byte[] pub33, long amount)
    {
        var scriptCode = P2pkhLockForPub(pub33);
        var digest = SighashForkId(tx, index, scriptCode, amount);
        var sig = Secp256k1.SignDigest(privSeed, digest);
        var der = Secp256k1.ToDer(sig);
        var sigWithType = der.Concat(new byte[] { SighashAllForkId }).ToArray();
        var scriptSig = Push(sigWithType).Concat(Push(pub33)).ToArray();
        var ins = tx.Ins.ToList();
        ins[index] = ins[index] with { ScriptSig = scriptSig };
        return tx with { Ins = ins };
    }

    /// <summary>
    /// Verify a signed P2PKH input's signature against its sighash (the core consensus check). Strict:
    /// the scriptSig must be exactly &lt;sig+hashtype&gt;&lt;pubkey&gt; with no trailing bytes, the pubkey must
    /// match, the hashtype must be exactly 0x41, and the signature must be canonical DER (low-S already
    /// enforced at signing). Built to the same strictness as the escrow path.
    /// </summary>
    public static bool VerifyP2pkhInput(Tx signed, int index, byte[] pub33, long amount)
    {
        try
        {
            if (index < 0 || index >= signed.Ins.Count) return false;
            var ss = signed.Ins[index].ScriptSig;
            var (sigType, p1) = ReadPush(ss, 0); if (sigType == null) return false;
            var (pk, p2) = ReadPush(ss, p1); if (pk == null) return false;
            if (p2 != ss.Length) return false;                       // no trailing bytes
            if (pk.Length != 33 || !pk.AsSpan().SequenceEqual(pub33)) return false; // pubkey must match the claimed key
            if (sigType.Length < 1 || sigType[^1] != SighashAllForkId) return false; // hashtype exactly 0x41
            var compact = StrictDerToCompact(sigType[..^1]); if (compact == null) return false;
            // recompute digest with empty scriptSig in the input (the FORKID sighash uses scriptCode, not scriptSig)
            var unsigned = signed with { Ins = signed.Ins.Select((i, k) => k == index ? i with { ScriptSig = Array.Empty<byte>() } : i).ToList() };
            var digest = SighashForkId(unsigned, index, P2pkhLockForPub(pub33), amount);
            return Secp256k1.VerifyDigest(pub33, digest, compact);
        }
        catch { return false; }
    }

    /// <summary>
    /// Build a pre-signed nLockTime RECOVERY: spend a funded P2PKH outpoint back to the owner, broadcastable
    /// only AFTER <paramref name="lockHeight"/> (non-final sequence so the locktime binds). The owner always
    /// holds this before risking funds, so no satoshi can be stranded.
    /// </summary>
    public static Tx BuildRecovery(string fundingTxid, uint vout, long amount, byte[] ownerSeed, byte[] ownerPub, long fee, uint lockHeight)
    {
        var outputs = new List<TxOut> { new(amount - fee, P2pkhLockForPub(ownerPub)) };
        var ins = new List<TxIn> { new(fundingTxid, vout, Array.Empty<byte>(), 0xfffffffe) }; // non-final → locktime active
        var tx = new Tx(2, ins, outputs, lockHeight);
        return SignP2pkhInput(tx, 0, ownerSeed, ownerPub, amount);
    }

    // ===================== 2-of-2 escrow: pot funding, cooperative settlement, unilateral recovery =====================
    // Two players fund the pot into a 2-of-2 output. The winner is paid by a COOPERATIVE settlement that
    // both sign. Before either player risks a satoshi, both co-sign an nLockTime RECOVERY that refunds each
    // funder their stake after a future height — so if a peer vanishes, funds are never stranded (the
    // always-recoverable rule). Every parser here is strict and fully bounds-checked.

    /// <summary>2-of-2 bare multisig lock: OP_2 &lt;pubA(33)&gt; &lt;pubB(33)&gt; OP_2 OP_CHECKMULTISIG.</summary>
    public static byte[] MultisigLock2of2(byte[] pubA, byte[] pubB)
    {
        if (pubA.Length != 33 || pubB.Length != 33) throw new ArgumentException("pubkeys must be 33-byte compressed");
        if ((pubA[0] != 0x02 && pubA[0] != 0x03) || (pubB[0] != 0x02 && pubB[0] != 0x03)) throw new ArgumentException("pubkeys must be compressed secp256k1");
        var b = new List<byte> { 0x52 };           // OP_2 (m)
        b.Add(33); b.AddRange(pubA);
        b.Add(33); b.AddRange(pubB);
        b.Add(0x52);                                // OP_2 (n)
        b.Add(0xae);                                // OP_CHECKMULTISIG
        return b.ToArray();
    }

    /// <summary>One signer's signature over a 2-of-2 escrow input: DER ‖ hashtype(0x41). LOW-S, random nonce.</summary>
    public static byte[] SignMultisig(Tx tx, int index, byte[] pubA, byte[] pubB, long amount, byte[] signerSeed)
    {
        var scriptCode = MultisigLock2of2(pubA, pubB);
        var digest = SighashForkId(tx, index, scriptCode, amount);
        var sig = Secp256k1.SignDigest(signerSeed, digest);
        return Secp256k1.ToDer(sig).Concat(new byte[] { SighashAllForkId }).ToArray();
    }

    /// <summary>Assemble the scriptSig for a signed 2-of-2 input: OP_0 &lt;sigA&gt; &lt;sigB&gt; (CHECKMULTISIG dummy + sigs in pubkey order).</summary>
    public static byte[] MultisigScriptSig(byte[] sigA, byte[] sigB)
    {
        if (sigA.Length is < 1 or > 75 || sigB.Length is < 1 or > 75) throw new ArgumentException("signature push out of range");
        var b = new List<byte> { 0x00 };           // OP_0 — the CHECKMULTISIG off-by-one dummy
        b.Add((byte)sigA.Length); b.AddRange(sigA);
        b.Add((byte)sigB.Length); b.AddRange(sigB);
        return b.ToArray();
    }

    public static Tx ApplyMultisigScriptSig(Tx tx, int index, byte[] sigA, byte[] sigB)
    {
        var ins = tx.Ins.ToList();
        ins[index] = ins[index] with { ScriptSig = MultisigScriptSig(sigA, sigB) };
        return tx with { Ins = ins };
    }

    /// <summary>Strictly verify a fully-signed 2-of-2 escrow input: both sigs valid, in pubkey order, hashtype 0x41, no trailing bytes.</summary>
    public static bool VerifyMultisig2of2(Tx signed, int index, byte[] pubA, byte[] pubB, long amount)
    {
        try
        {
            if (index < 0 || index >= signed.Ins.Count) return false;
            var ss = signed.Ins[index].ScriptSig;
            int p = 0;
            if (ss.Length < 1 || ss[p++] != 0x00) return false;               // OP_0 dummy required
            var (sigA, p1) = ReadPush(ss, p); if (sigA == null) return false; p = p1;
            var (sigB, p2) = ReadPush(ss, p); if (sigB == null) return false; p = p2;
            if (p != ss.Length) return false;                                 // reject trailing bytes
            if (sigA.Length < 1 || sigB.Length < 1) return false;
            if (sigA[^1] != SighashAllForkId || sigB[^1] != SighashAllForkId) return false; // hashtype exactly 0x41
            var compactA = StrictDerToCompact(sigA[..^1]);
            var compactB = StrictDerToCompact(sigB[..^1]);
            if (compactA == null || compactB == null) return false;
            var scriptCode = MultisigLock2of2(pubA, pubB);
            var unsigned = signed with { Ins = signed.Ins.Select((i, k) => k == index ? i with { ScriptSig = Array.Empty<byte>() } : i).ToList() };
            var digest = SighashForkId(unsigned, index, scriptCode, amount);
            // CHECKMULTISIG requires the sigs in the same order as the pubkeys
            return Secp256k1.VerifyDigest(pubA, digest, compactA) && Secp256k1.VerifyDigest(pubB, digest, compactB);
        }
        catch { return false; }
    }

    /// <summary>A cooperative settlement spending the 2-of-2 escrow to the winner (both peers then sign it).</summary>
    public static Tx BuildCooperativeSettlement(string escrowTxid, uint vout, long amount, byte[] winnerPub, long fee)
    {
        if (fee < 0 || fee >= amount) throw new ArgumentException("fee out of range");
        var outs = new List<TxOut> { new(amount - fee, P2pkhLockForPub(winnerPub)) };
        var ins = new List<TxIn> { new(escrowTxid, vout, Array.Empty<byte>(), 0xffffffff) };
        return new Tx(2, ins, outs, 0);
    }

    /// <summary>
    /// The pre-signed unilateral RECOVERY for a 2-of-2 escrow: refunds each funder their stake after
    /// <paramref name="lockHeight"/> (non-final sequence so the locktime binds). Both peers co-sign it
    /// BEFORE funding, so neither can strand the other's money.
    /// </summary>
    public static Tx BuildEscrowRecovery(string escrowTxid, uint vout, byte[] pubA, long stakeA, byte[] pubB, long stakeB, long fee, uint lockHeight)
    {
        if (fee < 0 || fee >= stakeB) throw new ArgumentException("fee out of range");
        var outs = new List<TxOut> { new(stakeA, P2pkhLockForPub(pubA)), new(stakeB - fee, P2pkhLockForPub(pubB)) };
        var ins = new List<TxIn> { new(escrowTxid, vout, Array.Empty<byte>(), 0xfffffffe) }; // non-final → locktime active
        return new Tx(2, ins, outs, lockHeight);
    }

    // ---- strict, bounds-checked parsers (new escrow code is built to consensus-grade strictness) ----

    /// <summary>The FORKID sighash digest a smart-contract CHECKSIG must sign (scriptCode = the contract script).</summary>
    public static byte[] ContractSighash(Tx tx, int index, byte[] scriptCode, long amount) => SighashForkId(tx, index, scriptCode, amount);

    /// <summary>Strict canonical-DER → 64-byte compact (r‖s), or null if non-canonical. For contract sig checking.</summary>
    public static byte[]? ParseStrictDer(byte[] der) => StrictDerToCompact(der);

    public const byte ContractHashType = SighashAllForkId; // 0x41

    /// <summary>Read a single canonical data push (1..75 bytes) at <paramref name="p"/>; returns (null, p) if malformed/OOB.</summary>
    private static (byte[]? data, int next) ReadPush(byte[] s, int p)
    {
        if (p < 0 || p >= s.Length) return (null, p);
        int len = s[p];
        if (len < 1 || len > 75) return (null, p);          // only small direct pushes are valid here
        if (p + 1 + len > s.Length) return (null, p);       // bounds
        return (s[(p + 1)..(p + 1 + len)], p + 1 + len);
    }

    /// <summary>
    /// Strict DER → 64-byte compact (r‖s). Enforces: 0x30 SEQUENCE, exact length byte, two 0x02 INTEGERs,
    /// canonical (no negative, no superfluous leading 0x00), no trailing bytes, and r,s in (0, n) implicitly
    /// via 32-byte fit. Returns null on any deviation. (The signature is also re-checked by ECDSA verify.)
    /// </summary>
    private static byte[]? StrictDerToCompact(byte[] der)
    {
        int p = 0;
        if (der.Length < 8 || der.Length > 72) return null;
        if (der[p++] != 0x30) return null;                  // SEQUENCE
        int seqLen = der[p++];
        if (seqLen != der.Length - 2) return null;          // length must cover exactly the rest
        var r = ReadDerInt(der, ref p); if (r == null) return null;
        var s = ReadDerInt(der, ref p); if (s == null) return null;
        if (p != der.Length) return null;                   // no trailing bytes
        // a canonical DER integer may carry a single 0x00 sign byte (length 33) when the magnitude's top
        // bit is set; strip it to fit the 32-byte field, and reject anything wider.
        r = StripSignByte(r); s = StripSignByte(s);
        if (r == null || s == null) return null;
        var compact = new byte[64];
        r.CopyTo(compact, 32 - r.Length);
        s.CopyTo(compact, 64 - s.Length);
        return compact;
    }

    private static byte[]? StripSignByte(byte[] v)
    {
        if (v.Length == 33 && v[0] == 0x00) v = v[1..]; // canonical sign byte for a high-bit magnitude
        return v.Length <= 32 ? v : null;
    }

    private static byte[]? ReadDerInt(byte[] der, ref int p)
    {
        if (p + 2 > der.Length || der[p++] != 0x02) return null; // INTEGER tag
        int len = der[p++];
        if (len < 1 || p + len > der.Length) return null;
        var v = der[p..(p + len)]; p += len;
        if ((v[0] & 0x80) != 0) return null;                              // must be non-negative (DER)
        if (v.Length > 1 && v[0] == 0x00 && (v[1] & 0x80) == 0) return null; // no superfluous leading zero
        return v;
    }
}
