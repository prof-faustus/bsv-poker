/**
 * Table screen (§A6.5) — the gameplay screen. It wires the ui-core presentational components
 * to the app-services LocalTableClient. The human acts; every action raises the signing modal
 * (§A6.7, no silent signing) before it is applied; the client auto-plays the bot and the hand
 * runs to showdown + settlement. All game logic is the real engine — this screen renders
 * view-models and emits actions only (no business logic, REQ-APP-052).
 */
import React, { useMemo, useRef, useState } from 'react';
import type { LocalTableClient, WalletService, WalletState } from '@bsv-poker/app-services';
import type { Action, Ruleset } from '@bsv-poker/protocol-types';
import {
  tableViewModel,
  showdownViewModel,
  settlementViewModel,
  signingPromptVM,
  actionFromChoice,
  type SigningPromptVM,
} from '@bsv-poker/ui-core/view-models';
import {
  MainnetBanner,
  PokerTable,
  ActionBar,
  TimerBanner,
  SigningModal,
  ShowdownPanel,
  SettlementSummary,
  WalletPanel,
} from '@bsv-poker/ui-core/components';

export function Table(props: {
  client: LocalTableClient;
  ruleset: Ruleset;
  /** The player's wallet — reachable at the table so they can fund/defund at any time. */
  wallet: WalletService;
  walletState: WalletState;
  /** Cash out the hero's remaining stack to the wallet, then leave. */
  onLeave: (heroStack: number) => void;
}): React.JSX.Element {
  const { client, ruleset, wallet, walletState } = props;
  const heroSeat = client.getHeroSeat();
  const [addAmount, setAddAmount] = useState(100);
  const [withdrawAmount, setWithdrawAmount] = useState(0);
  const [withdrawDest, setWithdrawDest] = useState('');

  // Re-render tick: the client mutates internally; bump this to project the new state.
  const [, setTick] = useState(0);
  const rerender = () => setTick((t) => t + 1);

  const [betAmount, setBetAmount] = useState(ruleset.blinds.bigBlind);
  const [prompt, setPrompt] = useState<SigningPromptVM | null>(null);
  const pendingAction = useRef<Action | null>(null);

  const state = client.getState();
  const legal = client.legalActions(heroSeat);
  const resolution = client.timeout();

  const vm = useMemo(
    () =>
      tableViewModel({
        state,
        heroSeat,
        heroHole: client.getHole(heroSeat),
        legal,
        resolution,
        decisionMs: ruleset.timeouts.decisionMs,
      }),
    // state identity changes whenever the client applies an action.
    [state, heroSeat, legal, resolution, ruleset.timeouts.decisionMs, client],
  );

  function requestAction(
    choice: 'fold' | 'check' | 'call' | 'bet' | 'raise',
    amount: number,
  ): void {
    const action = actionFromChoice(choice, heroSeat, legal, amount);
    pendingAction.current = action;
    const toCall = legal.call ? legal.call.amount : 0;
    setPrompt(signingPromptVM(action, { potBefore: vm.totalPot, toCall }));
  }

  function confirmAction(): void {
    const action = pendingAction.current;
    setPrompt(null);
    pendingAction.current = null;
    if (!action) return;
    client.apply(action);
    rerender();
  }

  function cancelAction(): void {
    setPrompt(null);
    pendingAction.current = null;
  }

  function nextHand(): void {
    client.startHand();
    setBetAmount(ruleset.blinds.bigBlind);
    rerender();
  }

  const showdown = state.handComplete
    ? showdownViewModel(state, client.getStartingStacks())
    : null;
  const settlement = state.handComplete
    ? settlementViewModel(state, client.getStartingStacks())
    : null;

  const heroStack = state.seats.find((s) => s.seat === heroSeat)?.stack ?? 0;

  return (
    <div style={{ maxWidth: 860, margin: '20px auto', padding: 16, display: 'grid', gap: 12 }}>
      <MainnetBanner regtest={ruleset.currency === 'play-regtest'} />

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h2 style={{ margin: 0 }}>
          Hold'em — blinds {ruleset.blinds.smallBlind}/{ruleset.blinds.bigBlind} (phase {state.phase})
        </h2>
        <button type="button" onClick={() => props.onLeave(heroStack)}>
          Cash out &amp; leave
        </button>
      </div>

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

      <PokerTable vm={vm} />
      <TimerBanner timer={vm.timer} />

      {!state.handComplete ? (
        <ActionBar
          vm={vm.actionBar}
          heroSeat={heroSeat}
          betAmount={betAmount}
          onBetAmountChange={setBetAmount}
          onAction={requestAction}
          pot={vm.totalPot}
        />
      ) : (
        <div style={{ display: 'grid', gap: 8 }}>
          {showdown && <ShowdownPanel vm={showdown} />}
          {settlement && <SettlementSummary vm={settlement} />}
          <button type="button" onClick={nextHand} style={{ padding: '8px 16px', fontSize: 16 }}>
            Deal next hand
          </button>
        </div>
      )}

      <SigningModal prompt={prompt} onConfirm={confirmAction} onCancel={cancelAction} />

      <details style={{ color: '#888', fontSize: 12 }}>
        <summary>Transcript / state hash (debug)</summary>
        <code>{client.stateHash()}</code>
      </details>
    </div>
  );
}
