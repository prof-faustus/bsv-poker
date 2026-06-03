package relay

import (
	"sync"
	"time"
)

// rateLimiter is a per-key token bucket (audit finding 9): it bounds how fast a single table can be
// published to, so an open relay cannot be trivially flooded/spammed. Auth (signatures) handles
// spoofing; this handles volume.
type rateLimiter struct {
	mu      sync.Mutex
	buckets map[string]*bucket
	rate    float64 // tokens added per second
	burst   float64 // max tokens
}

type bucket struct {
	tokens float64
	last   time.Time
}

func newRateLimiter(rate, burst float64) *rateLimiter {
	return &rateLimiter{buckets: make(map[string]*bucket), rate: rate, burst: burst}
}

// allow consumes one token for key, refilling by elapsed time; returns false when the bucket is dry.
func (l *rateLimiter) allow(key string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := time.Now()
	b, ok := l.buckets[key]
	if !ok {
		l.buckets[key] = &bucket{tokens: l.burst - 1, last: now}
		return true
	}
	b.tokens += l.rate * now.Sub(b.last).Seconds()
	if b.tokens > l.burst {
		b.tokens = l.burst
	}
	b.last = now
	if b.tokens >= 1 {
		b.tokens--
		return true
	}
	return false
}
