/*
 * lifecycle.c — implementation of the pure desktop-supervisor policy (see lifecycle.h).
 * Pure C, no platform headers, no allocation, no I/O — trivially testable and auditable.
 */
#include "lifecycle.h"
#include <string.h>

/* bsv-poker is fully peer-to-peer: there is NO relay/indexer server. The supervisor starts the chain
 * node, then the player's OWN local node (the P2P bridge the browser talks to on loopback), then the
 * settlement helper. Shutdown is the exact reverse. */
static const char* const STARTUP[]  = { "node", "local-node", "settlement" };
static const char* const SHUTDOWN[] = { "settlement", "local-node", "node" };
static const char* const IPC[]      = {
    "services_start", "services_stop", "services_status", "config_runtime", "config_set_network"
};

const char* const* bsv_startup_order(size_t* n) {
    if (n) *n = sizeof STARTUP / sizeof STARTUP[0];
    return STARTUP;
}

const char* const* bsv_shutdown_order(size_t* n) {
    if (n) *n = sizeof SHUTDOWN / sizeof SHUTDOWN[0];
    return SHUTDOWN;
}

int bsv_should_retry(unsigned attempt, unsigned max) {
    return attempt < max;
}

unsigned long bsv_backoff_ms(unsigned attempt) {
    unsigned shift = attempt < 6u ? attempt : 6u;          /* cap the shift so 1<<shift is bounded */
    unsigned long ms = 100ul * (1ul << shift);
    return ms < 5000ul ? ms : 5000ul;                       /* hard ceiling: never grows unbounded */
}

const char* const* bsv_ipc_commands(size_t* n) {
    if (n) *n = sizeof IPC / sizeof IPC[0];
    return IPC;
}

int bsv_is_known_ipc_command(const char* cmd) {
    if (!cmd) return 0;
    for (size_t i = 0; i < sizeof IPC / sizeof IPC[0]; ++i)
        if (strcmp(cmd, IPC[i]) == 0) return 1;
    return 0;
}

static void copy_err(char* err, size_t errlen, const char* msg) {
    if (err && errlen) {
        size_t i = 0;
        for (; msg[i] && i + 1 < errlen; ++i) err[i] = msg[i];
        err[i] = '\0';
    }
}

int bsv_validate_network_switch(const char* network, int mainnet_flag, char* err, size_t errlen) {
    if (err && errlen) err[0] = '\0';
    if (!network) { copy_err(err, errlen, "missing network (REQ-APP-026)"); return 1; }
    /* Same model on every network: regtest/play-regtest/testnet are test coins (always allowed);
     * mainnet (real funds) requires the explicit research flag. */
    if (strcmp(network, "regtest") == 0 || strcmp(network, "play-regtest") == 0 || strcmp(network, "testnet") == 0) return 0;
    if (strcmp(network, "mainnet") == 0) {
        if (mainnet_flag) return 0;
        copy_err(err, errlen, "mainnet requires the explicit research flag (REQ-APP-030)");
        return 1;
    }
    copy_err(err, errlen, "unrecognized network (REQ-APP-026)");
    return 1;
}

/* The runtime port map the UI reads (REQ-APP-027). bsv-poker is peer-to-peer: the UI talks to the
 * player's OWN local node (the P2P bridge) on loopback, plus the chain node. No relay/indexer ports. */
void bsv_runtime_ports(unsigned* local_node, unsigned* node) {
    if (local_node) *local_node = 8090u;
    if (node) *node = 18332u;
}

size_t bsv_data_subdir(const char* base, const char* kind, char* out, size_t outlen) {
    if (!base || !kind || !out || outlen == 0) return 0;
    size_t blen = strlen(base);
    while (blen > 0 && base[blen - 1] == '/') blen--;       /* trim trailing slashes on base */
    const char* mid = "/bsv-poker/";
    size_t need = blen + strlen(mid) + strlen(kind);
    if (need + 1 > outlen) return 0;                        /* fail-closed: never truncate silently */
    size_t p = 0;
    memcpy(out + p, base, blen); p += blen;
    memcpy(out + p, mid, strlen(mid)); p += strlen(mid);
    memcpy(out + p, kind, strlen(kind)); p += strlen(kind);
    out[p] = '\0';
    return p;
}
