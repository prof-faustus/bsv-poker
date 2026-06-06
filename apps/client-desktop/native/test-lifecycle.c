/*
 * test-lifecycle.c — unit tests for the pure desktop-supervisor policy (lifecycle.c). Console exe;
 * exits 0 iff every assertion passes, else 1. Mirrors the previous Rust #[test] suite 1:1 so the
 * desktop requirement claims (REQ-APP-020/021/022/024/026/027/028) keep positive AND negative
 * coverage after the move from Rust/Tauri to native C. Built + run by CI (tools/ci.ts).
 */
#include "lifecycle.h"
#include <stdio.h>
#include <string.h>

static int failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { printf("FAIL: %s\n", (msg)); failures++; } } while (0)

int main(void) {
    size_t n = 0;

    /* PEER-TO-PEER startup: node -> local-node (the player's OWN P2P node, not a relay/indexer) ->
     * settlement. There is no relay/indexer server. */
    const char* const* s = bsv_startup_order(&n);
    CHECK(n == 3, "startup order length");
    CHECK(strcmp(s[0], "node") == 0 && strcmp(s[1], "local-node") == 0 &&
          strcmp(s[2], "settlement") == 0, "startup order sequence (node -> local-node -> settlement)");

    /* shutdown is the exact reverse of startup */
    const char* const* d = bsv_shutdown_order(&n);
    CHECK(n == 3, "shutdown order length");
    for (size_t i = 0; i < 3; ++i) CHECK(strcmp(d[i], s[2 - i]) == 0, "shutdown is reverse of startup");

    /* bounded restart policy: true below cap, false at cap; loop provably terminates */
    CHECK(bsv_should_retry(0, BSV_MAX_RESTARTS), "retry at attempt 0");
    CHECK(bsv_should_retry(BSV_MAX_RESTARTS - 1, BSV_MAX_RESTARTS), "retry just below cap");
    CHECK(!bsv_should_retry(BSV_MAX_RESTARTS, BSV_MAX_RESTARTS), "no retry at cap (negative)");
    unsigned attempts = 0;
    while (bsv_should_retry(attempts, BSV_MAX_RESTARTS)) attempts++;
    CHECK(attempts == BSV_MAX_RESTARTS, "retry loop terminates exactly at cap");

    /* backoff increases then is hard-capped */
    CHECK(bsv_backoff_ms(1) > bsv_backoff_ms(0), "backoff increases 0->1");
    CHECK(bsv_backoff_ms(2) > bsv_backoff_ms(1), "backoff increases 1->2");
    CHECK(bsv_backoff_ms(100) <= 5000ul, "backoff capped at 5000ms (negative: no unbounded growth)");

    /* IPC command family is enumerated; unknown command rejected */
    bsv_ipc_commands(&n);
    CHECK(n == 5, "ipc command family size");
    CHECK(bsv_is_known_ipc_command("config_set_network"), "known ipc command accepted");
    CHECK(!bsv_is_known_ipc_command("evil_command"), "unknown ipc command rejected (negative)");

    /* network switch validated both sides */
    char err[128];
    CHECK(bsv_validate_network_switch("regtest", 0, err, sizeof err) == 0, "regtest allowed");
    CHECK(bsv_validate_network_switch("play-regtest", 0, err, sizeof err) == 0, "play-regtest allowed");
    CHECK(bsv_validate_network_switch("testnet", 0, err, sizeof err) == 0, "testnet allowed (same model, test coins)");
    CHECK(bsv_validate_network_switch("mainnet", 0, err, sizeof err) != 0, "mainnet refused without flag (negative)");
    CHECK(bsv_validate_network_switch("mainnet", 1, err, sizeof err) == 0, "mainnet allowed with research flag");
    CHECK(bsv_validate_network_switch("bogusnet", 1, err, sizeof err) != 0, "unrecognized network rejected (negative)");
    CHECK(bsv_validate_network_switch(NULL, 1, err, sizeof err) != 0, "null network rejected (negative)");

    /* runtime ports are distinct (read by the UI; not hard-coded there): the player's own local node
     * (P2P bridge) + the chain node. No relay/indexer ports. */
    unsigned ln, no;
    bsv_runtime_ports(&ln, &no);
    CHECK(ln != no, "runtime ports distinct (local-node vs chain node)");
    CHECK(ln == 8090u, "local node bridge on 8090");

    /* per-user data dir under base; trailing slash tolerated; fail-closed when too small */
    char buf[128];
    CHECK(bsv_data_subdir("/home/u/.local/share", "sqlite", buf, sizeof buf) > 0 &&
          strcmp(buf, "/home/u/.local/share/bsv-poker/sqlite") == 0, "data subdir composed");
    CHECK(bsv_data_subdir("/home/u/.local/share/", "node", buf, sizeof buf) > 0 &&
          strcmp(buf, "/home/u/.local/share/bsv-poker/node") == 0, "data subdir trims trailing slash");
    CHECK(bsv_data_subdir("/home/u", "sqlite", buf, 8) == 0, "data subdir fail-closed when too small (negative)");

    if (failures == 0) { printf("lifecycle tests: OK (all assertions passed)\n"); return 0; }
    printf("lifecycle tests: %d FAILURE(S)\n", failures);
    return 1;
}
