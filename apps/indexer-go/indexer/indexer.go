// Package indexer builds per-table projections of protocol transactions.
//
// REQ-NET-004 (core §8.4): via BS.node the platform ingests opaque
// protocol-transaction records and builds per-table projections (ordered tx
// list per table id, deduplicated by txid).
//
// REQ-NET-001 (core §8.1, P3): the indexer is a CONVENIENCE PROJECTION, never
// the source of truth. The truth is the validated transaction graph; the
// indexer must reconstruct an identical ordered set that any client can rebuild
// independently (P2, REQ-NET-007). Determinism is therefore the central
// contract: see Rebuild, which any client can run over the same record stream
// to obtain the same ordered txid list.
//
// It AUTHENTICATES records but does NOT ADJUDICATE the game: it never runs a
// poker engine or checks action legality (that is the engine's job, enforced at
// every client via assertLegal + deterministic replay). This is a deliberate P3
// property, not a gap — see docs/adr/0005-indexer-is-a-projection-not-an-adjudicator.md
// (audit findings #35/#36).
package indexer

import (
	"errors"
	"sort"
	"sync"
)

var (
	// ErrEmptyTxid rejects records without a txid (defensive validation).
	ErrEmptyTxid = errors.New("indexer: empty txid")
	// ErrEmptyTable rejects records without a table id.
	ErrEmptyTable = errors.New("indexer: empty table id")
)

// Record is an opaque protocol-transaction record ingested from BS.node.
// The indexer treats Raw as opaque bytes; it never parses game logic
// (REQ-NET-001). Class is an opaque tag the producer assigns.
type Record struct {
	Txid    string `json:"txid"`
	Class   string `json:"class"`
	TableID string `json:"tableId"`
	Raw     []byte `json:"raw,omitempty"`
}

// tableProjection holds the ordered, de-duplicated tx set for one table, plus (in validating mode)
// the authenticated-ingest state: the registered seat→pubkey map and the anti-equivocation pins.
type tableProjection struct {
	order []string            // txids in deterministic insertion order
	seen  map[string]struct{} // dedup set
	recs  map[string]Record   // full records by txid (for transcript rebuild / reconnect)

	// ---- validating-mode state (unused/empty in opaque mode) ----
	registered bool              // RegisterSeats has fixed this table's authoritative keys
	seatPubs   map[int]string    // seat → raw Ed25519 pubkey hex (the seating agreement)
	commitPins map[string]string // "seat:hand" → commitment hex (commit equivocation guard)
	revealPins map[string]string // "seat:hand" → reveal hex (reveal equivocation guard)
}

// Indexer is the concurrency-safe collection of per-table projections.
type Indexer struct {
	mu sync.Mutex
	// validate selects the ingest discipline for the WHOLE indexer (an explicit, process-wide mode —
	// never a silent per-table switch). When true, every ingest must be for a registered table and
	// must carry a well-formed, Ed25519-signed envelope that passes validateEnvelopeRecord, else it
	// is rejected (fail-closed). When false, the indexer is the legacy OPAQUE replay log.
	validate bool
	tables   map[string]*tableProjection
}

// New constructs an empty OPAQUE indexer (legacy replay log; REQ-NET-001).
func New() *Indexer {
	return &Indexer{tables: make(map[string]*tableProjection)}
}

// NewValidating constructs an indexer in VALIDATING mode (audit 7): ingest is authenticated and
// fail-closed. Tables must be registered (RegisterSeats) before any record is accepted.
func NewValidating() *Indexer {
	return &Indexer{validate: true, tables: make(map[string]*tableProjection)}
}

// RegisterSeats fixes the authoritative seat→pubkey map for a table (the lobby's signed seating
// agreement). It may be called once; a second call with a DIFFERENT map is refused (a seat's key is
// pinned for the table's life — never assume keys can rotate mid-table). Idempotent for an identical
// map. Only meaningful in validating mode.
func (ix *Indexer) RegisterSeats(tableID string, seatPubs map[int]string) error {
	if tableID == "" {
		return ErrEmptyTable
	}
	if len(seatPubs) == 0 {
		return errors.New("indexer: empty seat map")
	}
	ix.mu.Lock()
	defer ix.mu.Unlock()
	tp := ix.tables[tableID]
	if tp == nil {
		tp = newProjection()
		ix.tables[tableID] = tp
	}
	if tp.registered {
		// Refuse a conflicting re-registration; allow an identical one (idempotent).
		if !sameSeatMap(tp.seatPubs, seatPubs) {
			return errors.New("indexer: table already registered with a different seat map")
		}
		return nil
	}
	tp.registered = true
	tp.seatPubs = make(map[int]string, len(seatPubs))
	for k, v := range seatPubs {
		tp.seatPubs[k] = v
	}
	return nil
}

func newProjection() *tableProjection {
	return &tableProjection{
		seen:       make(map[string]struct{}),
		recs:       make(map[string]Record),
		commitPins: make(map[string]string),
		revealPins: make(map[string]string),
	}
}

func sameSeatMap(a, b map[int]string) bool {
	if len(a) != len(b) {
		return false
	}
	for k, v := range a {
		if b[k] != v {
			return false
		}
	}
	return true
}

// Ingest adds a record to its table's projection. Duplicate txids (per table)
// are ignored, preserving first-seen ordering. Returns true if the record was
// newly added, false if it was a duplicate. Determinism: ordering is strictly
// first-seen insertion order; replaying the same record sequence yields an
// identical ordered set (REQ-NET-007).
func (ix *Indexer) Ingest(rec Record) (bool, error) {
	if rec.Txid == "" {
		return false, ErrEmptyTxid
	}
	if rec.TableID == "" {
		return false, ErrEmptyTable
	}
	ix.mu.Lock()
	defer ix.mu.Unlock()
	tp := ix.tables[rec.TableID]
	if ix.validate {
		// Fail-closed: a validated indexer accepts records ONLY for a registered table, and ONLY when
		// the record carries an authentic, well-formed, signed envelope (validateEnvelopeRecord). A
		// rejected record mutates NOTHING (the equivocation pins are updated inside, after all checks
		// pass, so a rejection leaves the projection unchanged).
		if tp == nil || !tp.registered {
			return false, ErrNotRegistered
		}
		if err := validateEnvelopeRecord(rec, tp); err != nil {
			return false, err
		}
	} else if tp == nil {
		tp = newProjection()
		ix.tables[rec.TableID] = tp
	}
	if _, dup := tp.seen[rec.Txid]; dup {
		return false, nil
	}
	tp.seen[rec.Txid] = struct{}{}
	tp.order = append(tp.order, rec.Txid)
	tp.recs[rec.Txid] = rec
	return true, nil
}

// Records returns the FULL ordered records for a table (the transcript) so any client can
// rebuild current state from the valid tx set (REQ-NET-007, REQ-DATA-002/003). A copy is
// returned; an unknown table yields an empty slice.
func (ix *Indexer) Records(tableID string) []Record {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	tp := ix.tables[tableID]
	if tp == nil {
		return []Record{}
	}
	out := make([]Record, 0, len(tp.order))
	for _, id := range tp.order { // bounded by len(order)
		out = append(out, tp.recs[id])
	}
	return out
}

// Table returns the ordered txid list for a table id. A copy is returned so the
// caller cannot mutate internal state. An unknown table yields an empty slice.
func (ix *Indexer) Table(tableID string) []string {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	tp := ix.tables[tableID]
	if tp == nil {
		return []string{}
	}
	out := make([]string, len(tp.order))
	copy(out, tp.order)
	return out
}

// Tables returns the sorted list of known table ids (stable snapshot).
func (ix *Indexer) Tables() []string {
	ix.mu.Lock()
	defer ix.mu.Unlock()
	out := make([]string, 0, len(ix.tables))
	for id := range ix.tables { // bounded by len(tables)
		out = append(out, id)
	}
	sort.Strings(out)
	return out
}

// Rebuild deterministically reconstructs the ordered txid list for tableID from
// an arbitrary record stream, WITHOUT any indexer state. This is the function a
// client uses to verify the projection independently (P2, REQ-NET-007): given
// the same records in the same order, every client computes the same result.
// Records for other tables are ignored; duplicates keep first-seen position.
func Rebuild(tableID string, records []Record) []string {
	order := make([]string, 0, len(records))
	seen := make(map[string]struct{}, len(records))
	for _, rec := range records { // bounded by len(records)
		if rec.TableID != tableID || rec.Txid == "" {
			continue
		}
		if _, dup := seen[rec.Txid]; dup {
			continue
		}
		seen[rec.Txid] = struct{}{}
		order = append(order, rec.Txid)
	}
	return order
}
