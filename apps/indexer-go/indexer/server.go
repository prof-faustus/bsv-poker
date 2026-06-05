// HTTP transport for the indexer (stdlib only, zero external deps).
//
// REQ-NET-004 (core §8.4): serves per-table projections.
// REQ-NET-001 (core §8.1): the served list is a convenience projection (a hint
// to be confirmed by the client against the canonical tx graph), never truth.
package indexer

import (
	"encoding/json"
	"io"
	"net/http"
)

// Server wires an Indexer to HTTP handlers.
type Server struct {
	ix  *Indexer
	mux *http.ServeMux
}

// NewServer constructs an indexer HTTP server over a fresh OPAQUE Indexer (legacy replay log).
func NewServer() *Server {
	s := &Server{ix: New()}
	s.routes()
	return s
}

// NewServerMode constructs the server in opaque (validate=false) or VALIDATING (validate=true) mode.
// In validating mode, ingest is authenticated and fail-closed (audit 7); tables must register their
// seat→pubkey map first via POST /table/{id}/register.
func NewServerMode(validate bool) *Server {
	ix := New()
	if validate {
		ix = NewValidating()
	}
	s := &Server{ix: ix}
	s.routes()
	return s
}

// Index exposes the underlying indexer (for in-process ingestion/tests).
func (s *Server) Index() *Indexer { return s.ix }

// Handler returns the configured mux.
func (s *Server) Handler() http.Handler { return s.mux }

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) { s.mux.ServeHTTP(w, r) }

func (s *Server) routes() {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.handleHealth)
	mux.HandleFunc("POST /ingest", s.handleIngest)
	mux.HandleFunc("POST /table/{id}/register", s.handleRegister)
	mux.HandleFunc("GET /table/{id}", s.handleTable)
	mux.HandleFunc("GET /table/{id}/records", s.handleRecords)
	mux.HandleFunc("GET /tables", s.handleTables)
	s.mux = mux
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]string{"error": msg})
}

func (s *Server) handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

type registerReq struct {
	Seats []struct {
		Seat int    `json:"seat"`
		Pub  string `json:"pub"`
	} `json:"seats"`
}

// handleRegister fixes a table's authoritative seat→pubkey map for validating ingest (audit 7). This
// is the lobby's agreed seating, supplied once. Idempotent; a conflicting re-registration is refused.
func (s *Server) handleRegister(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if id == "" {
		writeErr(w, http.StatusBadRequest, ErrEmptyTable.Error())
		return
	}
	var req registerReq
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid body")
		return
	}
	seatPubs := make(map[int]string, len(req.Seats))
	for _, s := range req.Seats {
		seatPubs[s.Seat] = s.Pub
	}
	if err := s.ix.RegisterSeats(id, seatPubs); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"registered": true})
}

// handleIngest accepts a protocol-transaction record (REQ-NET-004). In validating mode the record is
// authenticated and structurally checked before it is stored (audit 7); a rejected record returns a
// 400 with the specific reason and mutates nothing.
func (s *Server) handleIngest(w http.ResponseWriter, r *http.Request) {
	var rec Record
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&rec); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid body")
		return
	}
	added, err := s.ix.Ingest(rec)
	if err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"added": added})
}

// tableResponse is the per-table projection view.
type tableResponse struct {
	TableID string   `json:"tableId"`
	Txids   []string `json:"txids"`
}

// handleTable returns the ordered, de-duplicated txid list for a table id.
// REQ-NET-001: this is a convenience projection, not the source of truth.
func (s *Server) handleTable(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if id == "" {
		writeErr(w, http.StatusBadRequest, ErrEmptyTable.Error())
		return
	}
	writeJSON(w, http.StatusOK, tableResponse{TableID: id, Txids: s.ix.Table(id)})
}

// handleRecords returns the FULL ordered records (the transcript) so a (re)connecting client
// can rebuild current state from the valid tx set (REQ-NET-007, REQ-DATA-002/003).
func (s *Server) handleRecords(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if id == "" {
		writeErr(w, http.StatusBadRequest, ErrEmptyTable.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"tableId": id, "records": s.ix.Records(id)})
}

func (s *Server) handleTables(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, s.ix.Tables())
}
