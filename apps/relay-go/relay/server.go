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
	"net/url"
	"os"
	"strings"
	"time"
)

// Server wires the registries to HTTP handlers.
type Server struct {
	Presence *PresenceRegistry
	Tables   *TableRegistry
	mux      *http.ServeMux
	pubLimit *rateLimiter      // per-table publish rate limit (audit 9)
	caps     *capabilityMinter // table-scoped capability tokens (audit 5)
}

// NewServer constructs a relay server with fresh registries.
// ttl is the presence heartbeat expiry window. The capability secret is taken from the RELAY_SECRET
// environment variable when set (so it can be rotated / shared across a restart); otherwise a fresh
// 32-byte CSPRNG secret is generated for this process.
func NewServer(ttl time.Duration) *Server {
	return NewServerWithSecret(ttl, []byte(os.Getenv("RELAY_SECRET")))
}

// NewServerWithSecret is NewServer with an explicit capability secret (deterministic tests / rotation).
func NewServerWithSecret(ttl time.Duration, secret []byte) *Server {
	s := &Server{
		Presence: NewPresenceRegistry(ttl),
		Tables:   NewTableRegistry(),
		// 50 publishes/sec sustained, burst 100, per table — bounds trivial spam/spoof floods.
		pubLimit: newRateLimiter(50, 100),
		caps:     newCapabilityMinter(secret),
	}
	s.routes()
	return s
}

// Handler exposes the configured mux behind CORS (also makes Server an http.Handler).
func (s *Server) Handler() http.Handler { return withCORS(s.mux) }

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) { withCORS(s.mux).ServeHTTP(w, r) }

// withCORS allows the browser web client (a different origin) to reach the relay over fetch/SSE
// (app §A4). The relay carries only opaque transport objects and is never the source of truth
// (REQ-NET-001) and every mutating route is capability-gated (audit 5) — but we still scope CORS to
// an ALLOWLIST as defense in depth (finding #31), rather than echoing `*` to every origin.
//
// Default allowlist (RELAY_ALLOWED_ORIGINS empty): loopback origins (http(s)://127.0.0.1|localhost|
// [::1] on any port — where the locally-served/dev web client runs) plus https://bsvpoker.local (the
// native desktop's WebView2 virtual host). RELAY_ALLOWED_ORIGINS overrides it: a comma list of exact
// origins, the token `loopback`, or `*` to restore the open policy for a public deployment that wants
// it. A request whose Origin is not permitted simply gets NO Access-Control-Allow-Origin header, so
// the browser blocks the cross-origin response; same-origin / non-browser callers (no Origin header)
// are unaffected.
func withCORS(next http.Handler) http.Handler {
	policy := parseCORSPolicy(os.Getenv("RELAY_ALLOWED_ORIGINS"))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		origin := r.Header.Get("Origin")
		if origin != "" && policy.permit(origin) {
			w.Header().Set("Access-Control-Allow-Origin", policy.echo(origin))
			w.Header().Add("Vary", "Origin")
			w.Header().Set("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS")
			w.Header().Set("Access-Control-Allow-Headers", "Content-Type, Accept")
		}
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// corsPolicy is the parsed CORS allowlist (see withCORS).
type corsPolicy struct {
	wildcard bool            // `*` — echo every origin (opt-in)
	loopback bool            // allow http(s)://127.0.0.1|localhost|[::1] on any port
	exact    map[string]bool // explicit allowed origins
}

func parseCORSPolicy(env string) corsPolicy {
	p := corsPolicy{exact: map[string]bool{}}
	if strings.TrimSpace(env) == "" {
		// Default: the locally-served web client (loopback) + the desktop WebView2 host.
		p.loopback = true
		p.exact["https://bsvpoker.local"] = true
		return p
	}
	for _, o := range strings.Split(env, ",") {
		switch o = strings.TrimSpace(o); o {
		case "":
			// skip empties
		case "*":
			p.wildcard = true
		case "loopback":
			p.loopback = true
		default:
			p.exact[o] = true
		}
	}
	return p
}

func (p corsPolicy) permit(origin string) bool {
	if p.wildcard || p.exact[origin] {
		return true
	}
	return p.loopback && isLoopbackOrigin(origin)
}

func (p corsPolicy) echo(origin string) string {
	if p.wildcard {
		return "*"
	}
	return origin // reflect the specific allowed origin (not `*`), with Vary: Origin
}

func isLoopbackOrigin(origin string) bool {
	u, err := url.Parse(origin)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") {
		return false
	}
	switch u.Hostname() {
	case "127.0.0.1", "localhost", "::1":
		return true
	default:
		return false
	}
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

	// Admission: mint a table-scoped capability token (audit 5).
	mux.HandleFunc("POST /tables/{id}/capability", s.handleMintCapability)

	// Tier B: opaque table-scoped object relay (capability-gated, fail-closed).
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
	// Admission is an optional shared secret. When present the table is GATED: a capability token
	// is only minted to a caller who presents this secret. Omitted → an OPEN table.
	Admission string `json:"admission,omitempty"`
}

// createTableResp returns the table plus the creator's capability token (the creator is admitted).
type createTableResp struct {
	ID      string `json:"id"`
	Name    string `json:"name"`
	Members int    `json:"members"`
	Token   string `json:"token"`
	Exp     int64  `json:"exp"`
}

func (s *Server) handleCreateTable(w http.ResponseWriter, r *http.Request) {
	var req createTableReq
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "invalid body")
		return
	}
	hash := ""
	if req.Admission != "" {
		hash = admissionHash(req.Admission)
	}
	t, err := s.Tables.CreateGated(req.ID, req.Name, hash)
	if err != nil {
		code := http.StatusBadRequest
		if err == ErrTableExists {
			code = http.StatusConflict
		}
		writeErr(w, code, err.Error())
		return
	}
	// The creator is admitted: mint a full pub+sub capability for the new table.
	token, exp, err := s.caps.mint(t.ID, ScopePubSub)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, "could not mint capability")
		return
	}
	writeJSON(w, http.StatusCreated, createTableResp{ID: t.ID, Name: t.Name, Members: t.SubscriberCount(), Token: token, Exp: exp})
}

type mintCapabilityReq struct {
	// Admission is the table's shared secret; required for a gated table, ignored for an open one.
	Admission string `json:"admission,omitempty"`
}

type mintCapabilityResp struct {
	Token string `json:"token"`
	Exp   int64  `json:"exp"`
}

// handleMintCapability issues a table-scoped pub+sub capability (audit 5). A gated table requires a
// matching admission secret (constant-time compare); an open table mints freely (the relay still
// REQUIRES the resulting token on publish/subscribe, so this bounds/labels every channel user and
// supports expiry + rotation). Fail-closed: a bad admission secret returns 403.
func (s *Server) handleMintCapability(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	t, err := s.Tables.Get(id)
	if err != nil {
		writeErr(w, http.StatusNotFound, err.Error())
		return
	}
	var req mintCapabilityReq
	// Body is optional for open tables; tolerate an empty body.
	if r.Body != nil {
		_ = json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&req)
	}
	if t.Gated() && !admissionMatches(t.AdmissionHash(), req.Admission) {
		writeErr(w, http.StatusForbidden, ErrBadAdmission.Error())
		return
	}
	token, exp, err := s.caps.mint(id, ScopePubSub)
	if err != nil {
		writeErr(w, http.StatusInternalServerError, "could not mint capability")
		return
	}
	writeJSON(w, http.StatusOK, mintCapabilityResp{Token: token, Exp: exp})
}

// bearerToken extracts a capability token from the Authorization: Bearer header, or (for SSE, where
// EventSource cannot set headers) from the `token` query parameter.
func bearerToken(r *http.Request) string {
	if h := r.Header.Get("Authorization"); h != "" {
		if after, ok := strings.CutPrefix(h, "Bearer "); ok {
			return strings.TrimSpace(after)
		}
	}
	return r.URL.Query().Get("token")
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
	// Capability required to publish (audit 5) — fail closed. Verified BEFORE rate-limit/body work so
	// an unauthorised caller is rejected at the door.
	if err := s.caps.verify(bearerToken(r), id, ScopePublish); err != nil {
		code := http.StatusUnauthorized
		if err == ErrBadCapability {
			code = http.StatusForbidden
		}
		writeErr(w, code, err.Error())
		return
	}
	if !s.pubLimit.allow(id) { // per-table publish quota (audit 9)
		writeErr(w, http.StatusTooManyRequests, "rate limited: table publish quota exceeded")
		return
	}
	// Fail closed on oversize (audit 6): read limit+1; if the extra byte exists the body exceeded the
	// bound — reject 413 rather than silently truncating + forwarding a corrupt frame.
	const maxBody = 1 << 20 // 1 MiB/object
	body, err := io.ReadAll(io.LimitReader(r.Body, maxBody+1))
	if err != nil {
		writeErr(w, http.StatusBadRequest, "read error")
		return
	}
	if len(body) > maxBody {
		writeErr(w, http.StatusRequestEntityTooLarge, "payload too large")
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
	// Capability required to subscribe (audit 5) — fail closed. Checked BEFORE joining the fan-out so
	// an unauthorised client never receives a single frame of the table's stream.
	if _, err := s.Tables.Get(id); err != nil {
		writeErr(w, http.StatusNotFound, err.Error())
		return
	}
	if err := s.caps.verify(bearerToken(r), id, ScopeSubscribe); err != nil {
		code := http.StatusUnauthorized
		if err == ErrBadCapability {
			code = http.StatusForbidden
		}
		writeErr(w, code, err.Error())
		return
	}
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
