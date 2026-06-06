/**
 * Connect screen (§A6.3) — framework-free (vanilla DOM): point the browser at the player's OWN local
 * node and connect to the lobby. bsv-poker is fully peer-to-peer: there is NO central relay. The
 * browser cannot be a raw network peer, so each player runs their own local node (tools/local-node.ts)
 * on loopback, which bridges the browser to the P2P mesh. The default URL is that local node
 * (http://127.0.0.1:8090). Explicit onClick (no <form> submit, REQ-UI-003). The REGTEST/play-money
 * banner is shown here and everywhere. The URL is a controlled input whose value lives in the app
 * model; the focus-preserving `mount` keeps the caret while typing across re-renders.
 */
import { el, type Child } from '@bsv-poker/ui-core/dom';
import { mainnetBanner } from '@bsv-poker/ui-core/components';

export interface ConnectProps {
  readonly defaultRelay: string;
  readonly relay: string;
  readonly onRelayChange: (value: string) => void;
  readonly identityId: string;
  readonly onConnect: (relay: string) => void;
  readonly connecting: boolean;
  readonly error: string | null;
}

export function connectScreen(p: ConnectProps): HTMLElement {
  const trimmed = p.relay.trim();
  const valid = /^https?:\/\//i.test(trimmed);

  return el('div', { style: { maxWidth: '520px', margin: '40px auto', padding: '16px', display: 'grid', gap: '12px' } },
    mainnetBanner(true),
    el('h1', { style: { margin: '0' } }, 'BSV Poker — Peer-to-Peer'),
    el('p', { style: { color: '#aaa', margin: '0' } },
      'This is fully peer-to-peer — there is NO central relay. Your browser connects to YOUR OWN local ' +
      'node (on this machine), which finds tables and carries play directly between players over the ' +
      'P2P mesh. The waiting room and interactive play are real; the on-chain crypto/transactions are ' +
      'the Node SDK path (§A2.3) and are not in this browser bundle.'),
    el('div', { style: { color: '#888', fontSize: '13px' } },
      'Your session identity: ', el('code', {}, p.identityId)),

    el('label', { style: { display: 'grid', gap: '4px' } }, 'Your local node URL',
      el('input', {
        type: 'url', 'aria-label': 'local node url', value: p.relay, placeholder: 'http://127.0.0.1:8090',
        onInput: (e: Event) => p.onRelayChange((e.target as HTMLInputElement).value),
        style: { padding: '6px', fontSize: '14px' },
      })),
    (!valid && trimmed.length > 0) ? el('div', { style: { color: '#f88', fontSize: '13px' } }, 'Enter a http(s):// URL.') : false as Child,
    p.error ? el('div', { style: { color: '#f88', fontSize: '13px' } }, p.error) : false as Child,

    el('div', { style: { display: 'flex', gap: '8px' } },
      el('button', {
        type: 'button', disabled: !valid || p.connecting, onClick: () => p.onConnect(trimmed),
        style: { padding: '8px 16px', fontSize: '16px' },
      }, p.connecting ? 'Connecting…' : 'Connect')),
  );
}
