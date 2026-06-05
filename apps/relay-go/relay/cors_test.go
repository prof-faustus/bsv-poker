// CORS allowlist tests (audit finding #31): the relay scopes Access-Control-Allow-Origin to an
// allowlist (default: loopback + the WebView2 host) instead of echoing `*`. Positive + negative.
package relay

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestCORSPolicyDefaultAllowsLoopbackAndWebView(t *testing.T) {
	p := parseCORSPolicy("")
	for _, o := range []string{"http://127.0.0.1:8091", "http://localhost:5173", "https://localhost", "http://[::1]:9000"} {
		if !p.permit(o) {
			t.Fatalf("default policy should permit loopback origin %q", o)
		}
		if p.echo(o) != o {
			t.Fatalf("an allowed origin must be reflected exactly, not echoed as *: got %q for %q", p.echo(o), o)
		}
	}
	if !p.permit("https://bsvpoker.local") {
		t.Fatal("default policy should permit the desktop WebView2 host")
	}
}

func TestCORSPolicyDefaultDeniesExternal(t *testing.T) {
	p := parseCORSPolicy("")
	for _, o := range []string{"https://evil.example", "http://attacker.test", "http://127.0.0.1.evil.com", "ftp://127.0.0.1"} {
		if p.permit(o) {
			t.Fatalf("default policy must DENY external origin %q", o)
		}
	}
}

func TestCORSPolicyWildcardAndExplicit(t *testing.T) {
	w := parseCORSPolicy("*")
	if !w.permit("https://anything.example") || w.echo("https://x") != "*" {
		t.Fatal("`*` policy must permit all origins and echo `*`")
	}
	e := parseCORSPolicy("https://poker.example, https://app.example")
	if !e.permit("https://poker.example") || !e.permit("https://app.example") {
		t.Fatal("explicit allowlist must permit each listed origin")
	}
	if e.permit("https://evil.example") || e.permit("http://127.0.0.1:8091") {
		t.Fatal("an explicit allowlist (no `loopback` token) must deny others, including loopback")
	}
	l := parseCORSPolicy("loopback")
	if !l.permit("http://127.0.0.1:9") || l.permit("https://evil.example") {
		t.Fatal("`loopback` token must permit loopback only")
	}
}

func TestCORSHeaderReflectedForAllowedDeniedForOther(t *testing.T) {
	h := NewServerWithSecret(time.Minute, []byte("secret")).Handler()

	allowed := httptest.NewRequest(http.MethodOptions, "/presence", nil)
	allowed.Header.Set("Origin", "http://127.0.0.1:8091")
	rr := httptest.NewRecorder()
	h.ServeHTTP(rr, allowed)
	if got := rr.Header().Get("Access-Control-Allow-Origin"); got != "http://127.0.0.1:8091" {
		t.Fatalf("an allowed origin should be reflected, got %q", got)
	}

	denied := httptest.NewRequest(http.MethodOptions, "/presence", nil)
	denied.Header.Set("Origin", "https://evil.example")
	rr2 := httptest.NewRecorder()
	h.ServeHTTP(rr2, denied)
	if got := rr2.Header().Get("Access-Control-Allow-Origin"); got != "" {
		t.Fatalf("a denied origin must receive NO Access-Control-Allow-Origin header, got %q", got)
	}
}
