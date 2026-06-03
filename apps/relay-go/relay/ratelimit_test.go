package relay

import (
	"testing"
	"time"
)

func TestRateLimiterCapsBurstAndRefills(t *testing.T) {
	// rate 10/s, burst 5: the first 5 are allowed, the 6th is denied.
	l := newRateLimiter(10, 5)
	allowed := 0
	for i := 0; i < 5; i++ {
		if l.allow("tableA") {
			allowed++
		}
	}
	if allowed != 5 {
		t.Fatalf("expected 5 allowed in the burst, got %d", allowed)
	}
	if l.allow("tableA") {
		t.Fatal("6th publish must be rate-limited (burst exhausted)")
	}
	// A different table has its own bucket.
	if !l.allow("tableB") {
		t.Fatal("a different table must not be limited by tableA's traffic")
	}
	// After enough time, tokens refill and publishing resumes.
	time.Sleep(250 * time.Millisecond) // ~2.5 tokens at 10/s
	if !l.allow("tableA") {
		t.Fatal("tokens should refill over time")
	}
}
