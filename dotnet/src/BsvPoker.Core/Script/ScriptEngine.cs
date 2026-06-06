using System.Numerics;
using BsvPoker.Crypto;

namespace BsvPoker.Core.Script;

/// <summary>
/// A Bitcoin (BSV) Script interpreter — the execution core for ALL on-chain smart contracts (auctions,
/// role bids, escrows, hash/time/threshold locks, payouts). Scripts are stack programs; a spend is valid
/// iff running scriptSig then scriptPubKey leaves a single truthy item. Strict and bounds-checked; hostile
/// input never throws out of <see cref="Verify"/> (it returns false). secp256k1 only; OP_RETURN aborts.
///
/// This is the foundation that lets "every bid be a full conditional smart contract," not a bare payment.
/// </summary>
public static class ScriptEngine
{
    // opcodes (subset sufficient for our contracts; extended as new contract forms are added)
    public const byte OP_0 = 0x00, OP_PUSHDATA1 = 0x4c, OP_PUSHDATA2 = 0x4d, OP_PUSHDATA4 = 0x4e, OP_1NEGATE = 0x4f;
    public const byte OP_1 = 0x51, OP_16 = 0x60;
    public const byte OP_NOP = 0x61, OP_IF = 0x63, OP_NOTIF = 0x64, OP_ELSE = 0x67, OP_ENDIF = 0x68, OP_VERIFY = 0x69, OP_RETURN = 0x6a;
    public const byte OP_DROP = 0x75, OP_DUP = 0x76, OP_NIP = 0x77, OP_OVER = 0x78, OP_SWAP = 0x7c, OP_2DROP = 0x6d, OP_2DUP = 0x6e;
    public const byte OP_EQUAL = 0x87, OP_EQUALVERIFY = 0x88;
    public const byte OP_1ADD = 0x8b, OP_1SUB = 0x8c, OP_NEGATE = 0x8f, OP_ABS = 0x90, OP_NOT = 0x91, OP_0NOTEQUAL = 0x92;
    public const byte OP_ADD = 0x93, OP_SUB = 0x94, OP_BOOLAND = 0x9a, OP_BOOLOR = 0x9b, OP_NUMEQUAL = 0x9c, OP_NUMEQUALVERIFY = 0x9d, OP_NUMNOTEQUAL = 0x9e;
    public const byte OP_LESSTHAN = 0x9f, OP_GREATERTHAN = 0xa0, OP_LESSTHANOREQUAL = 0xa1, OP_GREATERTHANOREQUAL = 0xa2, OP_MIN = 0xa3, OP_MAX = 0xa4, OP_WITHIN = 0xa5;
    public const byte OP_RIPEMD160 = 0xa6, OP_SHA256 = 0xa8, OP_HASH160 = 0xa9, OP_HASH256 = 0xaa;
    public const byte OP_CHECKSIG = 0xac, OP_CHECKSIGVERIFY = 0xad, OP_CHECKMULTISIG = 0xae, OP_CHECKMULTISIGVERIFY = 0xaf;
    public const byte OP_CHECKLOCKTIMEVERIFY = 0xb1, OP_CHECKSEQUENCEVERIFY = 0xb2;

    /// <summary>Supplies signature / locktime checks bound to the spending transaction (so contracts can gate on them).</summary>
    public interface IChecker
    {
        bool CheckSig(byte[] sig, byte[] pubKey);
        bool CheckLockTime(long lockTime);
        bool CheckSequence(long sequence);
    }

    /// <summary>A checker that fails all signature/locktime checks — for testing pure data/logic contracts.</summary>
    public sealed class NoChecker : IChecker
    {
        public bool CheckSig(byte[] sig, byte[] pubKey) => false;
        public bool CheckLockTime(long lockTime) => false;
        public bool CheckSequence(long sequence) => false;
    }

    private const int MaxStack = 1000;
    private const int MaxScriptElement = 100_000_000; // BSV: very large pushes allowed

    /// <summary>Verify a spend: run scriptSig, then scriptPubKey on the same stack; valid iff one truthy item remains.</summary>
    public static bool Verify(byte[] scriptSig, byte[] scriptPubKey, IChecker checker)
    {
        try
        {
            var stack = new List<byte[]>();
            if (!Eval(scriptSig, stack, checker)) return false;   // scriptSig must be push-only in policy; we allow ops but it is our own contract
            if (!Eval(scriptPubKey, stack, checker)) return false;
            return stack.Count > 0 && AsBool(stack[^1]);
        }
        catch { return false; }
    }

    /// <summary>Run a single script against the given stack. Returns false on any failure (VERIFY/RETURN/underflow).</summary>
    public static bool Eval(byte[] script, List<byte[]> stack, IChecker checker)
    {
        var alt = new List<byte[]>();
        var exec = new List<bool>(); // condition stack for IF/ELSE/ENDIF
        bool Executing() { foreach (var e in exec) if (!e) return false; return true; }
        int i = 0;
        while (i < script.Length)
        {
            bool go = Executing();
            byte op = script[i++];

            // data pushes
            if (op <= OP_PUSHDATA4)
            {
                int n;
                if (op < OP_PUSHDATA1) n = op;
                else if (op == OP_PUSHDATA1) { if (i + 1 > script.Length) return false; n = script[i++]; }
                else if (op == OP_PUSHDATA2) { if (i + 2 > script.Length) return false; n = script[i] | (script[i + 1] << 8); i += 2; }
                else { if (i + 4 > script.Length) return false; n = script[i] | (script[i + 1] << 8) | (script[i + 2] << 16) | (script[i + 3] << 24); i += 4; }
                if (n < 0 || n > MaxScriptElement || i + n > script.Length) return false;
                if (go) { Push(stack, script.AsSpan(i, n).ToArray()); }
                i += n;
                continue;
            }

            if (op == OP_IF || op == OP_NOTIF)
            {
                bool v = false;
                if (go) { if (stack.Count < 1) return false; v = AsBool(Pop(stack)); if (op == OP_NOTIF) v = !v; }
                exec.Add(v);
                continue;
            }
            if (op == OP_ELSE) { if (exec.Count == 0) return false; exec[^1] = !exec[^1]; continue; }
            if (op == OP_ENDIF) { if (exec.Count == 0) return false; exec.RemoveAt(exec.Count - 1); continue; }
            if (!go) continue; // skip everything else while in a non-taken branch

            switch (op)
            {
                case OP_0: Push(stack, Array.Empty<byte>()); break;
                case OP_1NEGATE: Push(stack, Num(-1)); break;
                case >= OP_1 and <= OP_16: Push(stack, Num(op - (OP_1 - 1))); break;
                case OP_NOP: break;
                case OP_VERIFY: if (stack.Count < 1 || !AsBool(Pop(stack))) return false; break;
                case OP_RETURN: return false;
                case OP_DROP: if (stack.Count < 1) return false; Pop(stack); break;
                case OP_2DROP: if (stack.Count < 2) return false; Pop(stack); Pop(stack); break;
                case OP_DUP: if (stack.Count < 1) return false; Push(stack, stack[^1]); break;
                case OP_2DUP: if (stack.Count < 2) return false; { var a = stack[^2]; var b = stack[^1]; Push(stack, a); Push(stack, b); } break;
                case OP_NIP: if (stack.Count < 2) return false; stack.RemoveAt(stack.Count - 2); break;
                case OP_OVER: if (stack.Count < 2) return false; Push(stack, stack[^2]); break;
                case OP_SWAP: if (stack.Count < 2) return false; (stack[^1], stack[^2]) = (stack[^2], stack[^1]); break;
                case OP_EQUAL: case OP_EQUALVERIFY:
                {
                    if (stack.Count < 2) return false;
                    bool eq = Pop(stack).AsSpan().SequenceEqual(Pop(stack));
                    if (op == OP_EQUALVERIFY) { if (!eq) return false; } else Push(stack, eq ? Num(1) : Array.Empty<byte>());
                    break;
                }
                case OP_RIPEMD160: if (stack.Count < 1) return false; Push(stack, Ripemd160.Hash(Pop(stack))); break;
                case OP_SHA256: if (stack.Count < 1) return false; Push(stack, Hashes.Sha256(Pop(stack))); break;
                case OP_HASH160: if (stack.Count < 1) return false; Push(stack, Hashes.Hash160(Pop(stack))); break;
                case OP_HASH256: if (stack.Count < 1) return false; Push(stack, Hashes.Sha256d(Pop(stack))); break;
                case OP_1ADD: UnaryNum(stack, x => x + 1); break;
                case OP_1SUB: UnaryNum(stack, x => x - 1); break;
                case OP_NEGATE: UnaryNum(stack, x => -x); break;
                case OP_ABS: UnaryNum(stack, x => x < 0 ? -x : x); break;
                case OP_NOT: UnaryNum(stack, x => x == 0 ? 1 : 0); break;
                case OP_0NOTEQUAL: UnaryNum(stack, x => x != 0 ? 1 : 0); break;
                case OP_ADD: BinNum(stack, (a, b) => a + b); break;
                case OP_SUB: BinNum(stack, (a, b) => a - b); break;
                case OP_BOOLAND: BinNum(stack, (a, b) => (a != 0 && b != 0) ? 1 : 0); break;
                case OP_BOOLOR: BinNum(stack, (a, b) => (a != 0 || b != 0) ? 1 : 0); break;
                case OP_NUMEQUAL: BinNum(stack, (a, b) => a == b ? 1 : 0); break;
                case OP_NUMNOTEQUAL: BinNum(stack, (a, b) => a != b ? 1 : 0); break;
                case OP_NUMEQUALVERIFY: BinNum(stack, (a, b) => a == b ? 1 : 0); if (stack.Count < 1 || !AsBool(Pop(stack))) return false; break;
                case OP_LESSTHAN: BinNum(stack, (a, b) => a < b ? 1 : 0); break;
                case OP_GREATERTHAN: BinNum(stack, (a, b) => a > b ? 1 : 0); break;
                case OP_LESSTHANOREQUAL: BinNum(stack, (a, b) => a <= b ? 1 : 0); break;
                case OP_GREATERTHANOREQUAL: BinNum(stack, (a, b) => a >= b ? 1 : 0); break;
                case OP_MIN: BinNum(stack, (a, b) => a < b ? a : b); break;
                case OP_MAX: BinNum(stack, (a, b) => a > b ? a : b); break;
                case OP_WITHIN: if (stack.Count < 3) return false; { var mx = AsNum(Pop(stack)); var mn = AsNum(Pop(stack)); var x = AsNum(Pop(stack)); Push(stack, (x >= mn && x < mx) ? Num(1) : Array.Empty<byte>()); } break;
                case OP_CHECKLOCKTIMEVERIFY: if (stack.Count < 1) return false; if (!checker.CheckLockTime((long)AsNum(stack[^1]))) return false; break; // leaves the value (NOP-like verify)
                case OP_CHECKSEQUENCEVERIFY: if (stack.Count < 1) return false; if (!checker.CheckSequence((long)AsNum(stack[^1]))) return false; break;
                case OP_CHECKSIG: case OP_CHECKSIGVERIFY:
                {
                    if (stack.Count < 2) return false;
                    var pub = Pop(stack); var sig = Pop(stack);
                    bool ok = checker.CheckSig(sig, pub);
                    if (op == OP_CHECKSIGVERIFY) { if (!ok) return false; } else Push(stack, ok ? Num(1) : Array.Empty<byte>());
                    break;
                }
                case OP_CHECKMULTISIG: case OP_CHECKMULTISIGVERIFY:
                {
                    if (stack.Count < 1) return false;
                    int nKeys = (int)AsNum(Pop(stack)); if (nKeys < 0 || nKeys > 20 || stack.Count < nKeys + 1) return false;
                    var keys = new List<byte[]>(); for (int k = 0; k < nKeys; k++) keys.Add(Pop(stack));
                    int nSigs = (int)AsNum(Pop(stack)); if (nSigs < 0 || nSigs > nKeys || stack.Count < nSigs + 1) return false;
                    var sigs = new List<byte[]>(); for (int k = 0; k < nSigs; k++) sigs.Add(Pop(stack));
                    if (stack.Count < 1) return false; Pop(stack); // CHECKMULTISIG off-by-one dummy
                    // greedy in-order match (keys and sigs both popped top-first → check in that order)
                    int si = 0, ki = 0; while (si < sigs.Count && ki < keys.Count) { if (checker.CheckSig(sigs[si], keys[ki])) si++; ki++; }
                    bool ok = si == sigs.Count;
                    if (op == OP_CHECKMULTISIGVERIFY) { if (!ok) return false; } else Push(stack, ok ? Num(1) : Array.Empty<byte>());
                    break;
                }
                default: return false; // unknown/disabled opcode
            }
            if (stack.Count > MaxStack) return false;
        }
        return exec.Count == 0; // all IFs closed
    }

    // ---- helpers ----
    private static void Push(List<byte[]> s, byte[] v) => s.Add(v);
    private static byte[] Pop(List<byte[]> s) { var v = s[^1]; s.RemoveAt(s.Count - 1); return v; }
    private static bool AsBool(byte[] v) { for (int i = 0; i < v.Length; i++) { if (v[i] != 0) { if (i == v.Length - 1 && v[i] == 0x80) return false; return true; } } return false; }

    private static void UnaryNum(List<byte[]> s, Func<BigInteger, BigInteger> f) { if (s.Count < 1) throw new InvalidOperationException(); var x = AsNum(Pop(s)); Push(s, Num(f(x))); }
    private static void BinNum(List<byte[]> s, Func<BigInteger, BigInteger, BigInteger> f) { if (s.Count < 2) throw new InvalidOperationException(); var b = AsNum(Pop(s)); var a = AsNum(Pop(s)); Push(s, Num(f(a, b))); }

    /// <summary>Script number = little-endian, sign-magnitude (high bit of last byte is the sign).</summary>
    public static BigInteger AsNum(byte[] v)
    {
        if (v.Length == 0) return BigInteger.Zero;
        var bytes = (byte[])v.Clone();
        bool neg = (bytes[^1] & 0x80) != 0;
        bytes[^1] &= 0x7f;
        BigInteger r = 0; for (int i = bytes.Length - 1; i >= 0; i--) r = (r << 8) | bytes[i];
        return neg ? -r : r;
    }

    public static byte[] Num(BigInteger n)
    {
        if (n == 0) return Array.Empty<byte>();
        bool neg = n < 0; var abs = neg ? -n : n;
        var bytes = new List<byte>();
        while (abs > 0) { bytes.Add((byte)(abs & 0xff)); abs >>= 8; }
        if ((bytes[^1] & 0x80) != 0) bytes.Add(neg ? (byte)0x80 : (byte)0x00);
        else if (neg) bytes[^1] |= 0x80;
        return bytes.ToArray();
    }
}
