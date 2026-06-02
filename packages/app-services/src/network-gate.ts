/**
 * Network-selection gate (core REQ-PROD-012; RT-02 F3). The platform is research/regtest by default;
 * mainnet is reachable ONLY behind an explicit, typed acknowledgement, and every selection surfaces
 * a banner the UI MUST display. This makes "mainnet behind an explicit flag" a tested code path, not
 * a convention.
 */

export type Network = 'play-regtest' | 'regtest' | 'mainnet';

export interface NetworkSelection {
  readonly network: Network;
  /** Human-facing banner the UI must show (REQ-PROD-012). */
  readonly banner: string;
  /** True only when mainnet was explicitly, correctly acknowledged. */
  readonly mainnetEnabled: boolean;
  readonly realFunds: boolean;
}

/** The exact token a caller must pass to enable mainnet — no funds move without it. */
export const MAINNET_ACK_TOKEN = 'I-UNDERSTAND-MAINNET-USES-REAL-FUNDS';

export function selectNetwork(opts?: { network?: Network; mainnetAck?: string }): NetworkSelection {
  const requested: Network = opts?.network ?? 'play-regtest';
  if (requested === 'mainnet') {
    if (opts?.mainnetAck !== MAINNET_ACK_TOKEN) {
      throw new Error(
        'mainnet is disabled by default and requires the explicit acknowledgement token ' +
          '(mainnetAck = MAINNET_ACK_TOKEN); refusing — this build is research/regtest only',
      );
    }
    return { network: 'mainnet', banner: '⚠ MAINNET — REAL FUNDS AT RISK (research use only)', mainnetEnabled: true, realFunds: true };
  }
  return {
    network: requested,
    banner: requested === 'regtest' ? '● REGTEST — test coins only, no real value' : '● PLAY-MONEY (regtest) — no real value',
    mainnetEnabled: false,
    realFunds: false,
  };
}
