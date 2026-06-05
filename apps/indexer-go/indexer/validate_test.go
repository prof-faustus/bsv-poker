// Validating-ingest tests (audit finding 7) — executable claims (INV-IXV-*).
//
// These prove the indexer authenticates what it stores: a forged/mislabelled/equivocating/
// unregistered record is rejected and mutates nothing, while a genuine signed envelope is accepted.
// The single most important test is INV-IXV-0: the canonical message the Go validator rebuilds is
// BYTE-IDENTICAL to what the TypeScript client signs (session-auth.ts envelopeMessage). If that ever
// drifts, real client signatures stop verifying — so it is pinned to a literal expected string.
package indexer

import (
	"crypto/ed25519"
	"encoding/hex"
	"encoding/json"
	"testing"
)

// signedRecord builds a Record whose Raw is the JSON envelope signed by priv over the canonical
// message — exactly what the live client produces.
func signedRecord(t *testing.T, priv ed25519.PrivateKey, tableID, txid string, e wireEnvelope) Record {
	t.Helper()
	msg, err := canonicalMessage(tableID, e)
	if err != nil {
		t.Fatalf("canonicalMessage: %v", err)
	}
	e.Sig = hex.EncodeToString(ed25519.Sign(priv, msg))
	raw, err := json.Marshal(e)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return Record{Txid: txid, Class: e.T, TableID: tableID, Raw: raw}
}

// INV-IXV-0: the canonical signed message matches TypeScript's JSON.stringify byte-for-byte.
func TestCanonicalMessageInteropVector(t *testing.T) {
	// session-auth.ts: JSON.stringify([tableId, t, seat, hand, kind??'', amount??0, c??'', r??'', discard??[], prev??''])
	got, err := canonicalMessage("t1", wireEnvelope{T: "commit", Seat: 0, Hand: 0, C: "ab"})
	if err != nil {
		t.Fatalf("canonicalMessage: %v", err)
	}
	want := `["t1","commit",0,0,"",0,"ab","",[],""]`
	if string(got) != want {
		t.Fatalf("canonical message drift:\n got=%s\nwant=%s", got, want)
	}
	// An action with an amount, discard and prev must also match exactly.
	got2, _ := canonicalMessage("tbl", wireEnvelope{T: "action", Seat: 2, Hand: 1, Kind: "bet", Amount: 50, Discard: []int{1, 3}, Prev: "ff"})
	want2 := `["tbl","action",2,1,"bet",50,"","",[1,3],"ff"]`
	if string(got2) != want2 {
		t.Fatalf("canonical message drift (action):\n got=%s\nwant=%s", got2, want2)
	}
}

// INV-IXV-1 (POSITIVE): a genuine signed envelope on a registered table is accepted.
func TestValidatingIngestAcceptsAuthentic(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(nil)
	ix := NewValidating()
	if err := ix.RegisterSeats("t1", map[int]string{0: hex.EncodeToString(pub)}); err != nil {
		t.Fatalf("register: %v", err)
	}
	rec := signedRecord(t, priv, "t1", "tx1", wireEnvelope{T: "commit", Seat: 0, Hand: 0, C: "abcd"})
	added, err := ix.Ingest(rec)
	if err != nil || !added {
		t.Fatalf("authentic ingest: added=%v err=%v", added, err)
	}
}

// INV-IXV-2 (NEGATIVE): a forged signature is rejected.
func TestValidatingIngestRejectsForgedSig(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(nil)
	_, evil, _ := ed25519.GenerateKey(nil) // a DIFFERENT key signs
	ix := NewValidating()
	_ = ix.RegisterSeats("t1", map[int]string{0: hex.EncodeToString(pub)})
	rec := signedRecord(t, evil, "t1", "tx1", wireEnvelope{T: "commit", Seat: 0, Hand: 0, C: "abcd"})
	_, err := ix.Ingest(rec)
	if err != ErrBadSignature {
		t.Fatalf("forged sig err = %v, want ErrBadSignature", err)
	}
	_ = priv
}

// INV-IXV-3 (NEGATIVE): a record for an unregistered seat is rejected.
func TestValidatingIngestRejectsUnknownSeat(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(nil)
	ix := NewValidating()
	_ = ix.RegisterSeats("t1", map[int]string{0: hex.EncodeToString(pub)})
	rec := signedRecord(t, priv, "t1", "tx1", wireEnvelope{T: "commit", Seat: 5, Hand: 0, C: "abcd"})
	if _, err := ix.Ingest(rec); err != ErrUnknownSeat {
		t.Fatalf("unknown seat err = %v, want ErrUnknownSeat", err)
	}
}

// INV-IXV-4 (NEGATIVE): an unregistered table rejects all ingest (fail-closed).
func TestValidatingIngestRejectsUnregisteredTable(t *testing.T) {
	ix := NewValidating()
	if _, err := ix.Ingest(Record{Txid: "x", Class: "commit", TableID: "ghost", Raw: []byte(`{"t":"commit","seat":0,"hand":0,"c":"ab"}`)}); err != ErrNotRegistered {
		t.Fatalf("unregistered table err = %v, want ErrNotRegistered", err)
	}
}

// INV-IXV-5 (NEGATIVE): a class tag that disagrees with the envelope type is rejected.
func TestValidatingIngestRejectsClassMismatch(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(nil)
	ix := NewValidating()
	_ = ix.RegisterSeats("t1", map[int]string{0: hex.EncodeToString(pub)})
	rec := signedRecord(t, priv, "t1", "tx1", wireEnvelope{T: "commit", Seat: 0, Hand: 0, C: "abcd"})
	rec.Class = "action" // mislabel
	if _, err := ix.Ingest(rec); err != ErrClassMismatch {
		t.Fatalf("class mismatch err = %v, want ErrClassMismatch", err)
	}
}

// INV-IXV-6 (NEGATIVE): commit equivocation — a second, conflicting commitment for the same
// (seat,hand) is rejected, and the first is left intact.
func TestValidatingIngestRejectsEquivocation(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(nil)
	ix := NewValidating()
	_ = ix.RegisterSeats("t1", map[int]string{0: hex.EncodeToString(pub)})
	r1 := signedRecord(t, priv, "t1", "tx1", wireEnvelope{T: "commit", Seat: 0, Hand: 0, C: "aaaa"})
	if _, err := ix.Ingest(r1); err != nil {
		t.Fatalf("first commit: %v", err)
	}
	r2 := signedRecord(t, priv, "t1", "tx2", wireEnvelope{T: "commit", Seat: 0, Hand: 0, C: "bbbb"})
	if _, err := ix.Ingest(r2); err != ErrEquivocation {
		t.Fatalf("equivocation err = %v, want ErrEquivocation", err)
	}
	// the first commit is still the only stored record for the table
	if recs := ix.Records("t1"); len(recs) != 1 {
		t.Fatalf("rejected equivocation must not mutate: %d records, want 1", len(recs))
	}
}

// INV-IXV-7 (NEGATIVE): an action must bind a prior state hash (prev).
func TestValidatingIngestRejectsActionWithoutPrev(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(nil)
	ix := NewValidating()
	_ = ix.RegisterSeats("t1", map[int]string{0: hex.EncodeToString(pub)})
	rec := signedRecord(t, priv, "t1", "tx1", wireEnvelope{T: "action", Seat: 0, Hand: 0, Kind: "bet", Amount: 1})
	if _, err := ix.Ingest(rec); err != ErrActionNoPrev {
		t.Fatalf("action without prev err = %v, want ErrActionNoPrev", err)
	}
}

// INV-IXV-8: opaque mode is unchanged (back-compat) — it accepts a bare record with no envelope.
func TestOpaqueModeStillAccepts(t *testing.T) {
	ix := New()
	if added, err := ix.Ingest(Record{Txid: "a", Class: "action", TableID: "t1"}); err != nil || !added {
		t.Fatalf("opaque ingest: added=%v err=%v", added, err)
	}
}

// INV-IXV-9: re-registration with a different seat map is refused; identical is idempotent.
func TestRegisterSeatsPinning(t *testing.T) {
	ix := NewValidating()
	if err := ix.RegisterSeats("t1", map[int]string{0: "aa"}); err != nil {
		t.Fatalf("first register: %v", err)
	}
	if err := ix.RegisterSeats("t1", map[int]string{0: "aa"}); err != nil {
		t.Fatalf("idempotent register should succeed: %v", err)
	}
	if err := ix.RegisterSeats("t1", map[int]string{0: "bb"}); err == nil {
		t.Fatal("conflicting re-registration must be refused")
	}
}

// INV-IXV-F1 (FUZZ): validateEnvelopeRecord never panics on arbitrary Raw bytes.
func FuzzValidateEnvelopeRecord(f *testing.F) {
	pub, _, _ := ed25519.GenerateKey(nil)
	tp := newProjection()
	tp.registered = true
	tp.seatPubs = map[int]string{0: hex.EncodeToString(pub)}
	f.Add([]byte(`{"t":"commit","seat":0,"hand":0,"c":"ab","sig":"00"}`))
	f.Add([]byte(""))
	f.Add([]byte("{"))
	f.Add([]byte(`{"t":"action","seat":0,"hand":0}`))
	f.Fuzz(func(t *testing.T, raw []byte) {
		// must never panic for any Raw; a fresh projection per call keeps it side-effect free
		local := newProjection()
		local.registered = true
		local.seatPubs = tp.seatPubs
		_ = validateEnvelopeRecord(Record{Txid: "x", Class: "commit", TableID: "t1", Raw: raw}, local)
	})
}
