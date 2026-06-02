// Command relay starts the bsv-poker Phase-1 hosted relay.
//
// REQ-NET-001 (core §8.1): the relay is transport + indexing only and is NEVER
// the source of truth. It binds to loopback and is supervised by the Tauri main
// process (app §A3.1, §A3.2).
//
// Run: go run . -addr 127.0.0.1:8091
package main

import (
	"context"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"time"

	"github.com/bsv-poker/bsv-poker/apps/relay-go/relay"
)

func main() {
	// Default port 8091: 8081 is taken on the build host.
	addr := flag.String("addr", "127.0.0.1:8091", "loopback listen address host:port")
	ttl := flag.Duration("presence-ttl", 30*time.Second, "presence heartbeat expiry window")
	sweep := flag.Duration("sweep-interval", 10*time.Second, "presence expiry sweep interval")
	flag.Parse()

	srv := relay.NewServer(*ttl)

	stop := make(chan struct{})
	go srv.RunSweeper(*sweep, stop)

	httpSrv := &http.Server{
		Addr:              *addr,
		Handler:           srv.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
	}

	// Graceful shutdown on SIGINT/SIGTERM (reverse-order stop, app §A3.2).
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()

	go func() {
		log.Printf("relay: listening on %s (transport/index only; never source of truth)", *addr)
		if err := httpSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("relay: listen error: %v", err)
		}
	}()

	<-ctx.Done()
	close(stop)
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer shutdownCancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Printf("relay: shutdown error: %v", err)
	}
	log.Printf("relay: stopped")
}
