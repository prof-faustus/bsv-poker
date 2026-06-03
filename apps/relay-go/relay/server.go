// HTTP + SSE transport for the relay (stdlib only, zero external deps).
//
// REQ-NET-001 (core §8.1): transport/index only, never the source of truth.
// REQ-NET-002 (core §8.2): Tier A (presence/tables) + Tier B (fan-out) APIs.
// app §A7.7: the connection manager can swap relay-discovery for a peer layer
// without UI change, so the wire surface here is deliberately minimal.
//
// Tier B delivery uses Server-Sent Events over net/http — a stdlib streaming
// channel — instead of a WebSocket dependency, keeping the module dependency-free.
package relay

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

// Server wires the registries to HTTP handlers.
type Server struct {
	Presence *PresenceRegistry
	Tables   *TableRegistry
	mux      *http.ServeMux
	pubLimit *rateLimiter // per-table publish rate limit (audit 9)
}

// NewServer constructs a relay server with fresh registries.
// ttl is the presence heartbeat expiry window.
func NewServer(ttl time.Duration) *Server {
	s := &Server{
		Presence: NewPresenceRegistry(ttl),
		Tables:   NewTableRegistry(),
		// 50 publishes/sec sustained, burst 100, per table — bounds trivial spam/spoof floods.
		pubLimit: newRateLimiter(50, 100),
	}
	s.routes()
	return s
}

// Handler exposes the configured mux behind CORS (also makes Server an http.Handler).
func (s *Server) Handler() http.Handler { return withCORS(s.mux) }

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) { withCORS(s.mux).ServeHTTP(w, r) }

// withCORS allows the browser web client (a different origin) to reach the relay over
// fetch/SSE (app §A4). The relay carries only opaque transport objects and is never the source
// of truth (REQ-NET-001), so a permissive cross-origin policy is acceptable for this transport.
func withCORS(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Accept")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (s *Server) routes() {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.handleHealth)

	// Tier A: presence (discovery).
	mux.HandleFunc("POST /presence", s.handleHeartbeat)
	mux.HandleFunc("DELETE /presence/{id}", s.handleLeave)
	mux.HandleFunc("GET /presence", s.handleListPresence)

	// Tier A: table directory.
	mux.HandleFunc("POST /tables", s.handleCreateTable)
	mux.HandleFunc("GET /tables", s.handleListTables)

	// Tier B: opaque table-scoped object relay.
	mux.HandleFunc("POST /tables/{id}/publish", s.handlePublish)
	mux.HandleFunc("GET /tables/{id}/subscribe", s.handleSubscribe)

	s.mux = mux
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	// Encode errors here are non-recoverable mid-response; best effort only.
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]string{"error": msg})
}

// REQ-NET-001: /healthz is the supervisor liveness probe (app §A3.2).
func (s *Server) handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

type heartbeatReq struct {
	PlayerID string `json:"playerId"`
	Addr     string `json:"addr"`
}

func (s *Server) handleHeartbeat(w http.ResponseWriter, r *http.Request) {
	var req heartbeatReq
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid body")
		return
	}
	if err := s.Presence.Heartbeat(req.PlayerID, req.Addr); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleLeave(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if id == "" {
		writeErr(w, http.StatusBadRequest, ErrEmptyID.Error())
		return
	}
	s.Presence.Remove(id)
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *Server) handleListPresence(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, s.Presence.List())
}

type createTableReq struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

func (s *Server) handleCreateTable(w http.ResponseWriter, r *http.Request) {
	var req createTableReq
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid body")
		return
	}
	t, err := s.Tables.Create(req.ID, req.Name)
	if err != nil {
		code := http.StatusBadRequest
		if err == ErrTableExists {
			code = http.StatusConflict
		}
		writeErr(w, code, err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, TableInfo{ID: t.ID, Name: t.Name, Members: t.SubscriberCount()})
}

func (s *Server) handleListTables(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, s.Tables.List())
}

// handlePublish forwards an opaque body to all table subscribers (Tier B).
// REQ-NET-001: the body is never parsed as game logic; it is stored/forwarded
// as bytes only.
func (s *Server) handlePublish(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	t, err := s.Tables.Get(id)
	if err != nil {
		writeErr(w, http.StatusNotFound, err.Error())
		return
	}
	if !s.pubLimit.allow(id) { // per-table publish quota (audit 9)
		writeErr(w, http.StatusTooManyRequests, "rate limited: table publish quota exceeded")
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20)) // bound: 1 MiB/object
	if err != nil {
		writeErr(w, http.StatusBadRequest, "read error")
		return
	}
	delivered := t.Publish(body)
	writeJSON(w, http.StatusOK, map[string]int{"delivered": delivered})
}

// handleSubscribe streams opaque table objects to the client over SSE until the
// client disconnects (Tier B fan-out). Each object is base64-free raw bytes
// framed as one SSE "data:" event; the relay does not interpret them.
func (s *Server) handleSubscribe(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	ch, unsub, err := s.Tables.Join(id)
	if err != nil {
		writeErr(w, http.StatusNotFound, err.Error())
		return
	}
	defer unsub()

	flusher, ok := w.(http.Flusher)
	if !ok {
		writeErr(w, http.StatusInternalServerError, "streaming unsupported")
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.WriteHeader(http.StatusOK)
	flusher.Flush()

	ctx := r.Context()
	keepalive := time.NewTicker(15 * time.Second)
	defer keepalive.Stop()

	for { // unbounded by design: a live streaming connection, gated by ctx.Done.
		select {
		case <-ctx.Done():
			return
		case <-keepalive.C:
			if _, err := io.WriteString(w, ": keepalive\n\n"); err != nil {
				return
			}
			flusher.Flush()
		case msg, open := <-ch:
			if !open {
				return
			}
			// SSE frame: one event carrying the opaque object as raw data.
			if _, err := fmt.Fprintf(w, "data: %s\n\n", msg); err != nil {
				return
			}
			flusher.Flush()
		}
	}
}

// RunSweeper runs the presence-expiry sweep on a bounded ticker until stop is
// closed (app §A7.2 heartbeat expiry). Intended to be launched in a goroutine.
func (s *Server) RunSweeper(interval time.Duration, stop <-chan struct{}) {
	if interval <= 0 {
		interval = 10 * time.Second
	}
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-stop:
			return
		case <-t.C:
			s.Presence.Sweep()
		}
	}
}
