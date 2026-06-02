/**
 * Connection modes (REQ-APP-040/041, §A4.1) and dual-path conflict surfacing (REQ-APP-074, core
 * §8.5). The client either talks to a bundled-local relay+node over loopback (developer/regtest,
 * the default) or a remote relay. When the speed path and the canonical path disagree, the canonical
 * value wins and the conflict is SURFACED — the speed path never silently overrides canonical.
 */

export type ConnectionMode = 'bundled-local' | 'remote-relay';

/**
 * The lobby permits creating/joining a table ONLY when the local services are READY (REQ-APP-023).
 * Any other supervisor state (starting/degraded/shutdown/fatal) gates table actions off.
 */
export function canCreateOrJoin(supervisorStatus: string): boolean {
  return supervisorStatus === 'ready';
}

export interface ConnectionSelection {
  readonly mode: ConnectionMode;
  readonly base: string;
  readonly loopback: boolean;
}

/** Resolve the connection mode. Desktop/regtest defaults to a loopback bundled-local companion. */
export function resolveConnectionMode(opts?: { environment?: 'desktop' | 'web'; relayUrl?: string }): ConnectionSelection {
  if (opts?.relayUrl) {
    const loopback = /^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])/.test(opts.relayUrl);
    return { mode: loopback ? 'bundled-local' : 'remote-relay', base: opts.relayUrl, loopback };
  }
  return { mode: 'bundled-local', base: 'http://127.0.0.1:8091', loopback: true };
}

export interface PathReading<T> {
  readonly speed?: T;
  readonly canonical?: T;
}

export interface Reconciled<T> {
  readonly value: T | undefined;
  readonly conflict: boolean;
  readonly source: 'canonical' | 'speed' | 'none';
}

/**
 * Reconcile a value seen on both paths. Canonical is authoritative; if the speed path differs from
 * canonical, that is a surfaced conflict (REQ-APP-074) — never silently overridden.
 */
export function reconcile<T>(r: PathReading<T>, eq: (a: T, b: T) => boolean = Object.is): Reconciled<T> {
  if (r.canonical !== undefined) {
    const conflict = r.speed !== undefined && !eq(r.speed, r.canonical);
    return { value: r.canonical, conflict, source: 'canonical' };
  }
  if (r.speed !== undefined) return { value: r.speed, conflict: false, source: 'speed' }; // provisional
  return { value: undefined, conflict: false, source: 'none' };
}
