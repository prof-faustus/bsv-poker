/**
 * Real-value production-readiness gate (audit finding #34). "Production ready for real value" must be
 * an ENFORCED, fail-closed property — not a claim. For a real-funds (mainnet) deployment EVERY one of
 * the production invariants below must hold or `assertRealValueReady` throws; a deployment calls it
 * before it will handle real value. For play-money / regtest there is no real value at risk, so only
 * the network selection itself must be valid.
 *
 * The invariants are the union of the audit's hard requirements, each already enforced elsewhere and
 * here composed into a single checkable gate:
 *   - NETWORK: mainnet only behind the explicit acknowledgement token (network-gate selectNetwork).
 *   - SIGNING MANDATORY: the unsigned escape hatch is OFF (also banned in shipped code by lint).
 *   - PRODUCTION SIGHASH: the real BIP-143/FORKID digest, never the simplified orchestration preimage.
 *   - REAL CUSTODY: a hardware/software Custody holds the keys — never the conformance fake or none.
 *   - MANAGED CAPABILITY SECRET: a stable RELAY_SECRET (capabilities survive restart, aren't ephemeral).
 *   - BIND HOST: local services bound to loopback unless non-loopback is explicitly opted in.
 */
import { selectNetwork, resolveBindHost, type Network } from './network-gate.ts';

export type SighashMode = 'bip143-forkid' | 'simplified';
export type CustodyKind = 'hardware' | 'software' | 'fake' | 'none';

export interface RealValueConfig {
  readonly network: Network;
  /** The explicit mainnet acknowledgement token (required to enable mainnet). */
  readonly mainnetAck?: string;
  /** True only when unsigned play/joins are disabled (the `allowUnsigned` hatch is off). */
  readonly signingRequired: boolean;
  /** Which sighash the value path signs — must be the real BIP-143/FORKID for real funds. */
  readonly sighash: SighashMode;
  /** What holds the keys — must be a real custody for real funds. */
  readonly custody: CustodyKind;
  /** True when a managed relay capability secret (RELAY_SECRET) is configured. */
  readonly relaySecretConfigured: boolean;
  /** Local-service bind host (default loopback). */
  readonly bindHost?: string;
  readonly allowNonLoopback?: boolean;
}

export interface ReadinessCheck {
  readonly name: string;
  readonly ok: boolean;
  readonly detail: string;
}

export interface ReadinessReport {
  readonly ready: boolean;
  /** True when this configuration puts REAL funds at risk (a satisfied mainnet selection). */
  readonly realFunds: boolean;
  readonly checks: readonly ReadinessCheck[];
  /** The required checks that failed (empty when ready). */
  readonly failures: readonly string[];
}

/** Evaluate every production invariant and return a report (never throws). */
export function evaluateRealValueReadiness(cfg: RealValueConfig): ReadinessReport {
  const checks: ReadinessCheck[] = [];
  const add = (name: string, ok: boolean, detail: string): void => { checks.push({ name, ok, detail }); };

  // NETWORK — mainnet only with the explicit ack; selectNetwork throws on an invalid mainnet request.
  let realFunds = false;
  try {
    const sel = selectNetwork({ network: cfg.network, ...(cfg.mainnetAck !== undefined ? { mainnetAck: cfg.mainnetAck } : {}) });
    realFunds = sel.realFunds;
    add('network', true, sel.banner);
  } catch (e) {
    add('network', false, (e as Error).message);
  }

  add('signing-mandatory', cfg.signingRequired === true, cfg.signingRequired ? 'unsigned play disabled' : 'allowUnsigned must be OFF for real funds');
  add('production-sighash', cfg.sighash === 'bip143-forkid', `sighash=${cfg.sighash} (must be bip143-forkid)`);
  add('real-custody', cfg.custody === 'hardware' || cfg.custody === 'software', `custody=${cfg.custody} (must be hardware/software, not fake/none)`);
  add('relay-secret', cfg.relaySecretConfigured === true, cfg.relaySecretConfigured ? 'managed capability secret configured' : 'a managed RELAY_SECRET must be configured');
  try {
    resolveBindHost({ host: cfg.bindHost ?? '127.0.0.1', ...(cfg.allowNonLoopback !== undefined ? { allowNonLoopback: cfg.allowNonLoopback } : {}) });
    add('bind-host', true, `bind ${cfg.bindHost ?? '127.0.0.1'}`);
  } catch (e) {
    add('bind-host', false, (e as Error).message);
  }

  // For REAL funds, every invariant is required. For play/regtest, only a valid network selection is
  // (no real value is at risk regardless of the other knobs).
  const required = realFunds ? checks : checks.filter((c) => c.name === 'network');
  const failures = required.filter((c) => !c.ok).map((c) => `${c.name}: ${c.detail}`);
  return { ready: failures.length === 0, realFunds, checks, failures };
}

/** Throw unless the deployment is production-ready for the configured network (fail-closed). */
export function assertRealValueReady(cfg: RealValueConfig): void {
  const r = evaluateRealValueReadiness(cfg);
  if (!r.ready) {
    throw new Error(
      `NOT production-ready for ${r.realFunds ? 'REAL FUNDS (mainnet)' : `network '${cfg.network}'`}: ${r.failures.join('; ')}`,
    );
  }
}
