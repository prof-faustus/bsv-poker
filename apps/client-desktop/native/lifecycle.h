/*
 * lifecycle.h — pure service-lifecycle policy for the bsv-poker desktop supervisor (app §A3.2).
 *
 * These are the EXECUTABLE CLAIMS the desktop requirements rest on, ported 1:1 from the previous
 * Rust supervisor's pure policy functions so the requirement coverage is preserved after Tauri was
 * removed (REQ-APP-020 lifecycle, 021 ordered start / reverse stop, 022 bounded restart + backoff,
 * 024 IPC command family, 026 both-sides network validation, 027 runtime ports, 028 per-user data
 * dir). Pure C, NO Win32 — so the same code drives the native host (main.c) AND a console unit test
 * (test-lifecycle.c). Every function has a positive AND negative assertion in the test.
 */
#ifndef BSV_LIFECYCLE_H
#define BSV_LIFECYCLE_H

#include <stddef.h>

/* Bounded restart cap (REQ-APP-022; NASA Power-of-Ten: the retry loop provably terminates). */
#define BSV_MAX_RESTARTS 5u

/* Ordered startup (REQ-APP-021): node -> indexer -> relay -> settlement. Returns the array and its
 * length via *n. shutdown_order is the exact reverse. The strings are static. */
const char* const* bsv_startup_order(size_t* n);
const char* const* bsv_shutdown_order(size_t* n); /* reverse of startup; static, reversed once. */

/* Bounded restart policy (REQ-APP-022): retry only while attempts remain strictly below the cap. */
int bsv_should_retry(unsigned attempt, unsigned max);

/* Exponential backoff in ms for restart attempt n, capped so it cannot grow without bound. */
unsigned long bsv_backoff_ms(unsigned attempt);

/* The recognized IPC command family (REQ-APP-024). An unlisted command is not dispatched. */
const char* const* bsv_ipc_commands(size_t* n);
int bsv_is_known_ipc_command(const char* cmd); /* 1 if in the family, else 0. */

/* Validate an inbound network-switch (REQ-APP-026 both-sides; REQ-APP-030 guard): regtest always
 * allowed; mainnet only with the explicit research flag; anything else rejected. Returns 0 on OK,
 * non-zero on rejection (writing a reason into err/errlen when err != NULL). */
int bsv_validate_network_switch(const char* network, int mainnet_flag, char* err, size_t errlen);

/* The runtime port map the UI reads (REQ-APP-027 — ports are not hard-coded in the UI). */
void bsv_runtime_ports(unsigned* relay, unsigned* indexer, unsigned* node);

/* Per-user data subdirectory (REQ-APP-028): "<base>/bsv-poker/<kind>", trailing slash on base
 * tolerated. Writes into out/outlen. Returns the number of chars written (excl NUL), or 0 on error. */
size_t bsv_data_subdir(const char* base, const char* kind, char* out, size_t outlen);

#endif /* BSV_LIFECYCLE_H */
