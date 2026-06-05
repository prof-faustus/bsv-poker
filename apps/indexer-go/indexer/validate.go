// Validating ingest for the indexer (audit finding 7).
//
// WHAT:
//   This file turns the indexer from an OPAQUE replay log into a VALIDATING one. For a table whose
//   authoritative seat→pubkey map has been registered, every ingested record must carry a
//   well-formed, Ed25519-SIGNED protocol envelope; the indexer reconstructs the exact signed
//   message, verifies the signature against the registered seat key, and enforces structural,
//   binding and anti-equivocation rules. Anything that fails is REJECTED (fail-closed) and never
//   enters the served transcript.
//
// HOW:
//   The producer (app-services session-auth.ts) signs the canonical message
//     JSON.stringify([tableId, t, seat, hand, kind??'', amount??0, c??'', r??'', discard??[], prev??''])
//   with a raw Ed25519 session key. We rebuild that byte string EXACTLY (compact JSON, HTML-escaping
//   DISABLED so '<' '>' '&' are not \u-escaped — JSON.stringify does not escape them) and call
//   crypto/ed25519.Verify against the 32-byte registered public key for env.seat.
//
// WHY (and why THIS design, not the alternatives):
//   A reconnecting client rebuilds state from the indexer's transcript. If the indexer accepts
//   forged/replayed/corrupt envelopes, that client rebuilds from poisoned data (audit 7). So the
//   indexer must authenticate what it stores.
//   WHY NOT validate full game legality / settlement here: that would require a SECOND, independent
//   poker engine written in Go. Two engines inevitably diverge on some edge case, and a divergence
//   between the ingest validator and the client engine is a consensus split — strictly WORSE than
//   no second check. The canonical deterministic engine (TypeScript) is the single source of game
//   truth; the client replays the AUTHENTICATED transcript through it (transcript.ts). The indexer's
//   job is therefore exactly the game-rule-AGNOSTIC layer: authenticity, structure, binding,
//   non-equivocation. This split is deliberate and is the security boundary of this module.
//
// SECURITY BOUNDARY:
//   trusted inputs:   the registered seat→pub map (established out-of-band from the lobby's signed
//                     seating agreement) and the process's own code. NOTHING else.
//   untrusted inputs: every ingested record and every byte of its Raw envelope — hostile until
//                     validated here.
//   recoverable errors: any validation failure → the record is rejected with a 4xx; the indexer
//                     keeps running and its existing transcript is unchanged (no partial mutation).
//   fatal errors:     none in this path (no panics on hostile input — see FuzzValidate).
//   rejection conditions: see validateEnvelopeRecord below (enumerated).
//   side effects:     on success only, the record is appended to the table projection; equivocation
//                     pins (seat,hand,kind)→commitment are recorded. No mutation on any failure.
//
// WHAT MUST NEVER BE ASSUMED:
//   - never assume Raw is JSON, bounded, or even UTF-8 — it is attacker bytes;
//   - never assume a record's claimed seat is the signer — the signature proves it or it is rejected;
//   - never assume a registered table's keys can change — re-registration with different keys is
//     refused (a seat's key is pinned for the table's life; see RegisterSeats).
package indexer

import (
	"bytes"
	"crypto/ed25519"
	"encoding/hex"
	"encoding/json"
	"errors"
	"strconv"
)

// Validation rejection reasons (each is an enumerated, testable failure condition).
var (
	ErrNotRegistered  = errors.New("indexer: table not registered for validated ingest")
	ErrBadEnvelope    = errors.New("indexer: malformed envelope")
	ErrUnknownSeat    = errors.New("indexer: envelope seat is not a registered seat")
	ErrBadSignature   = errors.New("indexer: envelope signature failed verification")
	ErrClassMismatch  = errors.New("indexer: record class does not match envelope type")
	ErrBadCommit      = errors.New("indexer: commit envelope missing/!hex commitment")
	ErrBadReveal      = errors.New("indexer: reveal envelope missing/!hex reveal")
	ErrActionNoPrev   = errors.New("indexer: action envelope missing prior-state binding (prev)")
	ErrEquivocation   = errors.New("indexer: seat equivocated (conflicting commitment for hand)")
	ErrEnvelopeTooBig = errors.New("indexer: envelope exceeds size bound")
)

// maxEnvelopeBytes bounds the decoded envelope (CWE-400). Real envelopes are a few hundred bytes.
const maxEnvelopeBytes = 64 * 1024

// wireEnvelope mirrors the app-layer signed envelope (message-validation.ts WireEnvelope + sig).
// Every field is optional on the wire and defaulted to match the producer's `?? ''/0/[]` rule.
type wireEnvelope struct {
	T       string  `json:"t"`
	Seat    int     `json:"seat"`
	Hand    int     `json:"hand"`
	Kind    string  `json:"kind"`
	Amount  float64 `json:"amount"`
	C       string  `json:"c"`
	R       string  `json:"r"`
	Discard []int   `json:"discard"`
	Prev    string  `json:"prev"`
	Sig     string  `json:"sig"`
}

// parseEnvelope decodes Raw bytes into a wireEnvelope, bounded and non-throwing (returns error,
// never panics). Unknown/extra fields are ignored; the canonical message uses only the named ones.
func parseEnvelope(raw []byte) (wireEnvelope, error) {
	var e wireEnvelope
	if len(raw) == 0 || len(raw) > maxEnvelopeBytes {
		return e, ErrEnvelopeTooBig
	}
	dec := json.NewDecoder(bytes.NewReader(raw))
	if err := dec.Decode(&e); err != nil {
		return e, ErrBadEnvelope
	}
	if e.T != "commit" && e.T != "reveal" && e.T != "action" {
		return e, ErrBadEnvelope
	}
	if e.Seat < 0 || e.Hand < 0 {
		return e, ErrBadEnvelope
	}
	return e, nil
}

// canonicalMessage rebuilds the EXACT bytes the producer signed (session-auth.ts envelopeMessage):
//
//	JSON.stringify([tableId, t, seat, hand, kind??'', amount??0, c??'', r??'', discard??[], prev??''])
//
// compact, with HTML escaping DISABLED so the byte string matches JSON.stringify for all inputs.
func canonicalMessage(tableID string, e wireEnvelope) ([]byte, error) {
	discard := e.Discard
	if discard == nil {
		discard = []int{} // JS `?? []` → an empty array, which must marshal as [] not null
	}
	arr := []interface{}{tableID, e.T, e.Seat, e.Hand, e.Kind, e.Amount, e.C, e.R, discard, e.Prev}
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false) // JSON.stringify does NOT \u-escape < > & — match it exactly
	if err := enc.Encode(arr); err != nil {
		return nil, err
	}
	// json.Encoder appends a trailing '\n'; the signed message has none.
	return bytes.TrimRight(buf.Bytes(), "\n"), nil
}

// verifyEnvelopeSig verifies env.Sig over the canonical message against the registered seat pubkey.
// Returns false on any malformed key/sig/message — never panics (bounds-checked decode).
func verifyEnvelopeSig(tableID, pubHex string, e wireEnvelope) bool {
	if e.Sig == "" || pubHex == "" {
		return false
	}
	pub, err := hex.DecodeString(pubHex)
	if err != nil || len(pub) != ed25519.PublicKeySize {
		return false
	}
	sig, err := hex.DecodeString(e.Sig)
	if err != nil || len(sig) != ed25519.SignatureSize {
		return false
	}
	msg, err := canonicalMessage(tableID, e)
	if err != nil {
		return false
	}
	return ed25519.Verify(ed25519.PublicKey(pub), msg, sig)
}

// isHexNonEmpty reports whether s is a non-empty, even-length string of hex digits (commitments and
// reveals must be hex — matches the client's isHex trust-boundary check).
func isHexNonEmpty(s string) bool {
	if len(s) == 0 || len(s)%2 != 0 {
		return false
	}
	_, err := hex.DecodeString(s)
	return err == nil
}

// validateEnvelopeRecord is the stateful gate for a single record on a REGISTERED table. It performs
// every game-rule-AGNOSTIC check and returns nil only if the record is authentic and well-formed.
//
// Order of checks (cheapest/most-fundamental first; all are side-effect-free until the very end):
//  1. structure — Raw decodes to a known envelope type with non-negative seat/hand;
//  2. class binding — the record's Class tag matches the envelope's actual type (no mislabelling);
//  3. seat authenticity — env.seat is a REGISTERED seat, and the signature verifies against THAT
//     seat's pinned Ed25519 key over the exact canonical message (no forging another seat);
//  4. per-type structure — commit carries hex c, reveal carries hex r, action binds a prior state
//     hash (prev) so it cannot be replayed against a different transcript position;
//  5. non-equivocation — a seat may commit/reveal at most ONE value per hand; a second, conflicting
//     commitment/reveal for the same (seat,hand) is rejected.
//
// SIDE EFFECTS: only on success, and only at the very end, the equivocation pins are recorded. A
// rejection at any step mutates nothing — the projection is left exactly as it was (the "no state
// mutation on a rejected hostile input" rule).
func validateEnvelopeRecord(rec Record, tp *tableProjection) error {
	e, err := parseEnvelope(rec.Raw)
	if err != nil {
		return err
	}
	if rec.Class != e.T {
		return ErrClassMismatch
	}
	pub, ok := tp.seatPubs[e.Seat]
	if !ok || pub == "" {
		return ErrUnknownSeat
	}
	if !verifyEnvelopeSig(rec.TableID, pub, e) {
		return ErrBadSignature
	}
	key := strconv.Itoa(e.Seat) + ":" + strconv.Itoa(e.Hand)
	switch e.T {
	case "commit":
		if !isHexNonEmpty(e.C) {
			return ErrBadCommit
		}
		if prev, seen := tp.commitPins[key]; seen && prev != e.C {
			return ErrEquivocation
		}
	case "reveal":
		if !isHexNonEmpty(e.R) {
			return ErrBadReveal
		}
		if prev, seen := tp.revealPins[key]; seen && prev != e.R {
			return ErrEquivocation
		}
	case "action":
		if e.Prev == "" {
			return ErrActionNoPrev
		}
	}
	// All checks passed — NOW record the equivocation pins (the only mutation in this function).
	switch e.T {
	case "commit":
		tp.commitPins[key] = e.C
	case "reveal":
		tp.revealPins[key] = e.R
	}
	return nil
}
