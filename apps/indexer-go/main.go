// Command indexer starts the bsv-poker table-transaction indexer.
//
// REQ-NET-004 (core §8.4): ingests opaque protocol-transaction records and
// serves per-table projections.
// REQ-NET-001 (core §8.1): convenience projection only, never the source of
// truth. Supervised by the Tauri main process (app §A3.1, started BEFORE the
// relay per the ordered-startup rule REQ-APP-021).
//
// Run: go run . -addr 127.0.0.1:8092
package main

import (
	"context"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"time"

	"github.com/bsv-poker/bsv-poker/apps/indexer-go/indexer"
)

func main() {
	addr := flag.String("addr", "127.0.0.1:8092", "loopback listen address host:port")
	flag.Parse()

	srv := indexer.NewServer()
	httpSrv := &http.Server{
		Addr:              *addr,
		Handler:           srv.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
	}

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()

	go func() {
		log.Printf("indexer: listening on %s (convenience projection; never source of truth)", *addr)
		if err := httpSrv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("indexer: listen error: %v", err)
		}
	}()

	<-ctx.Done()
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer shutdownCancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Printf("indexer: shutdown error: %v", err)
	}
	log.Printf("indexer: stopped")
}
