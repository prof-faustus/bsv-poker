/**
 * Practice lobby / create-local-table screen (§A6.3/§A6.4). Pick blinds/stack → start a local
 * heads-up Hold'em table vs the bot. No <form> submit — explicit onClick (REQ-UI-003). Validation
 * comes from the ui-core view-model; this screen only renders and emits. The wallet panel is shown
 * here too so the player can top up before buying in to the practice table.
 */
import React, { useState } from 'react';
import { WalletService, type WalletState } from '@bsv-poker/app-services';
import {
  validateTableCreate,
  type TableCreateForm,
} from '@bsv-poker/ui-core/view-models';
import { MainnetBanner, WalletPanel } from '@bsv-poker/ui-core/components';

export function Lobby(props: {
  wallet: WalletService;
  walletState: WalletState;
  onStart: (form: TableCreateForm, botOpponents: number) => void;
  onBack: () => void;
}): React.JSX.Element {
  const { wallet, walletState } = props;
  const [smallBlind, setSmallBlind] = useState(1);
  const [bigBlind, setBigBlind] = useState(2);
  const [startingStack, setStartingStack] = useState(100);
  const [decisionMs, setDecisionMs] = useState(30000);
  const [botOpponents, setBotOpponents] = useState(1); // YOU choose how many bots to play

  const [addAmount, setAddAmount] = useState(100);
  const [withdrawAmount, setWithdrawAmount] = useState(0);
  const [withdrawDest, setWithdrawDest] = useState('');

  const form: TableCreateForm = { smallBlind, bigBlind, startingStack, decisionMs };
  const validation = validateTableCreate(form);
  const canAfford = walletState.balance >= startingStack;

  return (
    <div style={{ maxWidth: 560, margin: '40px auto', padding: 16, display: 'grid', gap: 12 }}>
      <MainnetBanner regtest={true} />
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1 style={{ margin: 0, fontSize: 22 }}>Practice vs bots (offline)</h1>
        <button type="button" onClick={props.onBack}>
          Back
        </button>
      </div>
      <p style={{ color: '#aaa', margin: 0 }}>
        No-Limit Texas Hold'em against the number of bots YOU choose, played in your browser on the
        real game engine — no relay needed. You always play your own seat; the bots fill the others.
        Starting your table buys in for the starting stack from your wallet; leaving cashes out.
      </p>

      <WalletPanel
        snapshot={walletState}
        addAmount={addAmount}
        onAddAmountChange={setAddAmount}
        onAddFunds={(amount) => void wallet.addFunds(amount)}
        withdrawAmount={withdrawAmount}
        onWithdrawAmountChange={setWithdrawAmount}
        withdrawDest={withdrawDest}
        onWithdrawDestChange={setWithdrawDest}
        onWithdraw={(amount, dest) => void wallet.withdraw(amount, dest)}
        compact
      />

      <div role="group" aria-label="create table" style={{ display: 'grid', gap: 10 }}>
        <label>
          Small blind{' '}
          <input type="number" min={1} value={smallBlind} onChange={(e) => setSmallBlind(Number(e.target.value))} />
        </label>
        <label>
          Big blind{' '}
          <input type="number" min={2} value={bigBlind} onChange={(e) => setBigBlind(Number(e.target.value))} />
        </label>
        <label>
          Starting stack{' '}
          <input
            type="number"
            min={4}
            value={startingStack}
            onChange={(e) => setStartingStack(Number(e.target.value))}
          />
        </label>
        <label>
          Decision time (ms){' '}
          <input
            type="number"
            min={1000}
            step={1000}
            value={decisionMs}
            onChange={(e) => setDecisionMs(Number(e.target.value))}
          />
        </label>
        <label>
          Bot opponents (you choose){' '}
          <select value={botOpponents} onChange={(e) => setBotOpponents(Number(e.target.value))}>
            {[1, 2, 3, 4, 5, 6, 7, 8].map((n) => (
              <option key={n} value={n}>
                {n} bot{n > 1 ? 's' : ''} ({n + 1}-handed)
              </option>
            ))}
          </select>
        </label>

        {!validation.ok && (
          <ul style={{ color: '#f88' }}>
            {validation.errors.map((e) => (
              <li key={e}>{e}</li>
            ))}
          </ul>
        )}
        {validation.ok && !canAfford && (
          <div style={{ color: '#f88', fontSize: 13 }}>
            Insufficient balance to buy in for {startingStack} (have {walletState.balance}). Add funds
            above.
          </div>
        )}

        <button
          type="button"
          disabled={!validation.ok || !canAfford}
          onClick={() => props.onStart(form, botOpponents)}
          style={{ padding: '8px 16px', fontSize: 16 }}
        >
          Buy in &amp; start vs {botOpponents} bot{botOpponents > 1 ? 's' : ''}
        </button>
      </div>
    </div>
  );
}
