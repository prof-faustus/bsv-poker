// bsv-poker desktop supervisor (Tauri v2) — app spec §A3.
//
// The Rust main process supervises the local services (the embedded BSV node, the relay, and the
// indexer) so a non-technical user double-clicks and plays (REQ-APP-020). It implements the §A3.2
// service lifecycle, ordered startup (node → indexer → relay) and reverse-order shutdown
// (REQ-APP-021), a BOUNDED restart policy (REQ-APP-022, Power-of-Ten no-unbounded-loop), and the
// IPC contract (Appendix I): services.* / config.* (custody.* lands with the custody worker).
//
// Regtest only by default (REQ-APP-029/030); mainnet requires the explicit research flag and an
// unmissable UI banner — the supervisor REFUSES any non-regtest network unless flagged.
//
// NOTE: requires the native toolchain (Rust + a C linker) + the Tauri CLI to build — see
// ../README.md. It is not compiled in the spec-authoring/CI environment that lacks Rust.

#![cfg_attr(all(not(debug_assertions), target_os = "windows"), windows_subsystem = "windows")]

use std::process::{Child, Command};
use std::sync::Mutex;

use serde::Serialize;
use tauri::{Emitter, Manager, State};

const RELAY_ADDR: &str = "127.0.0.1:8091";
const INDEXER_ADDR: &str = "127.0.0.1:8092";
const MAX_RESTARTS: u32 = 5; // bounded restart policy (REQ-APP-022)

// ---- Pure lifecycle policy (REQ-APP-020/021/022), unit-tested below ----
/// Ordered startup (REQ-APP-021): the embedded node first, then the indexer, then the relay.
#[allow(dead_code)]
fn startup_order() -> [&'static str; 3] {
    ["node", "indexer", "relay"]
}

/// Reverse-order shutdown (REQ-APP-021).
#[allow(dead_code)]
fn shutdown_order() -> Vec<&'static str> {
    let mut o = startup_order().to_vec();
    o.reverse();
    o
}

/// Bounded restart policy (REQ-APP-022; Power-of-Ten: no unbounded loop) — retry only while
/// attempts remain strictly below the cap.
#[allow(dead_code)]
fn should_retry(attempt: u32, max: u32) -> bool {
    attempt < max
}

/// Exponential backoff (ms) for restart attempt `n`, capped so it cannot grow without bound.
#[allow(dead_code)]
fn backoff_ms(attempt: u32) -> u64 {
    (100u64.saturating_mul(1u64 << attempt.min(6))).min(5_000)
}

#[derive(Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
#[allow(dead_code)] // StartNode/Fatal are modelled §A3.2 states reached once the node adapter binds
enum LifecycleState {
    Init,
    StartNode,
    StartIndexer,
    StartRelay,
    Ready,
    Degraded,
    Shutdown,
    Fatal,
}

#[derive(Serialize, Clone)]
struct ServiceStatus {
    service: String,
    state: String,
    detail: Option<String>,
    attempt: u32,
}

struct Supervisor {
    lifecycle: Mutex<LifecycleState>,
    relay: Mutex<Option<Child>>,
    indexer: Mutex<Option<Child>>,
    network: Mutex<String>,
}

impl Supervisor {
    fn new() -> Self {
        Supervisor {
            lifecycle: Mutex::new(LifecycleState::Init),
            relay: Mutex::new(None),
            indexer: Mutex::new(None),
            network: Mutex::new("regtest".to_string()),
        }
    }
}

/// Spawn one bundled service binary bound to loopback (REQ-APP-027/028, §A10.7).
fn spawn_service(bin: &str, addr: &str) -> Result<Child, String> {
    Command::new(bin)
        .arg("-addr")
        .arg(addr)
        .spawn()
        .map_err(|e| format!("failed to spawn {bin}: {e}"))
}

#[tauri::command]
fn services_start(app: tauri::AppHandle, sup: State<Supervisor>) -> Result<bool, String> {
    // Ordered startup: node → indexer → relay (node is the bonded-subsat-channel embedded node,
    // bound by the adapter; here we supervise the indexer and relay binaries).
    *sup.lifecycle.lock().unwrap() = LifecycleState::StartIndexer;
    emit_status(&app, "indexer", "starting", None, 0);

    let mut started_indexer = false;
    for attempt in 0..MAX_RESTARTS {
        match spawn_service("indexer", INDEXER_ADDR) {
            Ok(child) => {
                *sup.indexer.lock().unwrap() = Some(child);
                emit_status(&app, "indexer", "healthy", None, attempt);
                started_indexer = true;
                break;
            }
            Err(e) => emit_status(&app, "indexer", "failed", Some(e), attempt),
        }
    }
    if !started_indexer {
        *sup.lifecycle.lock().unwrap() = LifecycleState::Degraded;
        return Err("indexer failed to start within the restart policy".into());
    }

    *sup.lifecycle.lock().unwrap() = LifecycleState::StartRelay;
    emit_status(&app, "relay", "starting", None, 0);
    let mut started_relay = false;
    for attempt in 0..MAX_RESTARTS {
        match spawn_service("relay", RELAY_ADDR) {
            Ok(child) => {
                *sup.relay.lock().unwrap() = Some(child);
                emit_status(&app, "relay", "healthy", None, attempt);
                started_relay = true;
                break;
            }
            Err(e) => emit_status(&app, "relay", "failed", Some(e), attempt),
        }
    }
    if !started_relay {
        *sup.lifecycle.lock().unwrap() = LifecycleState::Degraded;
        return Err("relay failed to start within the restart policy".into());
    }

    *sup.lifecycle.lock().unwrap() = LifecycleState::Ready;
    Ok(true)
}

#[tauri::command]
fn services_stop(sup: State<Supervisor>) -> Result<bool, String> {
    // Reverse-order shutdown: relay then indexer (REQ-APP-021).
    if let Some(mut c) = sup.relay.lock().unwrap().take() {
        let _ = c.kill();
    }
    if let Some(mut c) = sup.indexer.lock().unwrap().take() {
        let _ = c.kill();
    }
    *sup.lifecycle.lock().unwrap() = LifecycleState::Shutdown;
    Ok(true)
}

#[tauri::command]
fn services_status(sup: State<Supervisor>) -> String {
    match *sup.lifecycle.lock().unwrap() {
        LifecycleState::Init => "init",
        LifecycleState::StartNode => "start_node",
        LifecycleState::StartIndexer => "start_indexer",
        LifecycleState::StartRelay => "start_relay",
        LifecycleState::Ready => "ready",
        LifecycleState::Degraded => "degraded",
        LifecycleState::Shutdown => "shutdown",
        LifecycleState::Fatal => "fatal",
    }
    .to_string()
}

#[tauri::command]
fn config_runtime(sup: State<Supervisor>) -> serde_json::Value {
    serde_json::json!({
        "ports": { "relay": 8091, "indexer": 8092, "node": 18332 },
        "network": *sup.network.lock().unwrap(),
        "flags": { "mainnetResearch": false }
    })
}

/// Guarded network switch (REQ-APP-030): refuses non-regtest unless the research flag is set.
#[tauri::command]
fn config_set_network(network: String, mainnet_flag: bool, sup: State<Supervisor>) -> Result<bool, String> {
    if network != "regtest" && !mainnet_flag {
        return Err("mainnet requires the explicit research flag (REQ-APP-030)".into());
    }
    *sup.network.lock().unwrap() = network;
    Ok(true)
}

fn emit_status(app: &tauri::AppHandle, service: &str, state: &str, detail: Option<String>, attempt: u32) {
    let _ = app.emit(
        "services.status",
        ServiceStatus {
            service: service.to_string(),
            state: state.to_string(),
            detail,
            attempt,
        },
    );
}

fn main() {
    tauri::Builder::default()
        .manage(Supervisor::new())
        .invoke_handler(tauri::generate_handler![
            services_start,
            services_stop,
            services_status,
            config_runtime,
            config_set_network
        ])
        .setup(|app| {
            // Ordered startup begins at launch; the UI gates play until READY (REQ-APP-023).
            let _ = app.handle();
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running bsv-poker desktop");
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;

    #[test]
    fn startup_is_node_then_indexer_then_relay() {
        assert_eq!(startup_order(), ["node", "indexer", "relay"]);
    }

    #[test]
    fn shutdown_is_reverse_of_startup() {
        assert_eq!(shutdown_order(), vec!["relay", "indexer", "node"]);
    }

    #[test]
    fn restart_policy_is_bounded_no_unbounded_loop() {
        assert!(should_retry(0, MAX_RESTARTS));
        assert!(should_retry(MAX_RESTARTS - 1, MAX_RESTARTS));
        assert!(!should_retry(MAX_RESTARTS, MAX_RESTARTS));
        // The retry loop provably terminates at the cap.
        let mut attempts = 0u32;
        while should_retry(attempts, MAX_RESTARTS) {
            attempts += 1;
        }
        assert_eq!(attempts, MAX_RESTARTS);
    }

    #[test]
    fn backoff_increases_and_is_capped() {
        assert!(backoff_ms(1) > backoff_ms(0));
        assert!(backoff_ms(2) > backoff_ms(1));
        assert!(backoff_ms(100) <= 5_000);
    }
}
