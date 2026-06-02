/**
 * Networked table (§A6.5/§A7) — REAL multiplayer play over the relay. It constructs an
 * InteractiveNetworkedTableClient from the seated result, drives React state from its onUpdate
 * stream, renders seats/board/pot/timer via ui-core, and on the hero's turn raises the signing
 * modal (no silent signing, §A6.7) before calling client.submitAction(). client.play() resolves
 * when the hand completes → we show the showdown + settlement. Hole cards are rendered for the
 * hero's own seat (which we know). No game logic here — legality is read from the engine via the
 * update's `legal` / client.legalActions() (REQ-APP-052).
 *
 * The InteractiveNetworkedTableClient plays exactly ONE hand per instance (the entropy
 * commit/reveal handshake is per-hand); after settlement the player returns to the lobby. A
 * multi-hand session would re-run the handshake per hand — that is out of scope here and noted.
 */
import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  InteractiveNetworkedTableClient,
  type ClientUpdate,
  type SeatedResult,
} from '@bsv-poker/app-services';
import { RelayClient } from '@bsv-poker/app-services';
import type { Action } from '@bsv-poker/protocol-types';
import type { HoldemState } from '@bsv-poker/game-holdem';
import {
  tableViewModel,
  showdownViewModel,
  settlementViewModel,
  signingPromptVM,
  actionFromChoice,
  networkSeatLabel,
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
import type { WalletService, WalletState } from '@bsv-poker/app-services';

export function NetworkTable(props: {
  relay: string;
  tableId: string;
  tableName: string;
  seated: SeatedResult;
  /** The player's wallet — reachable at the table so they can fund/defund at any time. */
  wallet: WalletService;
  walletState: WalletState;
  /** Cash out the hero's remaining stack (final or current) to the wallet, then leave. */
  onLeave: (heroStack: number) => void;
}): React.JSX.Element {
  const { seated, wallet, walletState } = props;
  const heroSeat = seated.mySeat;
  const ruleset = seated.ruleset;
  const [addAmount, setAddAmount] = useState(100);
  const [withdrawAmount, setWithdrawAmount] = useState(0);
  const [withdrawDest, setWithdrawDest] = useState('');

  const startingStacks = useMemo(
    () => new Map(seated.seats.map((s) => [s.seat, s.stack])),
    [seated.seats],
  );

  // Construct the client once (per mount). play() runs the handshake then the hand.
  const clientRef = useRef<InteractiveNetworkedTableClient | null>(null);
  if (clientRef.current === null) {
    const entropy = new Uint8Array(32);
    (globalThis.crypto as Crypto).getRandomValues(entropy);
    clientRef.current = new InteractiveNetworkedTableClient({
      relay: new RelayClient(props.relay),
      tableId: props.tableId,
      mySeat: seated.mySeat,
      seats: seated.seats,
      ruleset: seated.ruleset,
      entropy,
    });
  }
  const client = clientRef.current;

  const [update, setUpdate] = useState<ClientUpdate | null>(null);
  const [finalState, setFinalState] = useState<HoldemState | null>(null);
  const [status, setStatus] = useState('Agreeing the deck (commit/reveal handshake)…');
  const [error, setError] = useState<string | null>(null);

  const [betAmount, setBetAmount] = useState(ruleset.blinds.bigBlind);
  const [prompt, setPrompt] = useState<SigningPromptVM | null>(null);
  const pendingAction = useRef<Action | null>(null);

  useEffect(() => {
    const off = client.onUpdate((u) => {
      setUpdate(u);
      if (!u.complete) setStatus('');
    });
    let cancelled = false;
    // A continuous table: play hand after hand (fresh shuffle, carried stacks, rotating button)
    // until the player leaves (client.abort()) or the table can't continue.
    client
      .playSession({ maxHands: 100 })
      .then(() => {
        // The interactive client is variant-generic (GameState); this screen renders the
        // holdem-shaped projection. The runtime shape matches; narrow at the boundary.
        const s = client.getState();
        if (!cancelled && s) setFinalState(s as HoldemState);
      })
      .catch((e) => {
        if (!cancelled) setError((e as Error).message);
      });
    return () => {
      cancelled = true;
      client.abort();
      off();
    };
    // client is stable for the life of this component.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // GameState (variant-generic) narrowed to the holdem-shaped state this screen renders.
  const state = (update?.state ?? null) as HoldemState | null;
  const heroHole = state ? (state.hole?.[heroSeat] ?? []) : [];
  const legal = update?.yourTurn ? client.legalActions() : null;

  const vm = useMemo(() => {
    if (!state) return null;
    return tableViewModel({
      state,
      heroSeat,
      heroHole,
      // When it is not our turn there are no legal actions for us; pass an empty descriptor.
      legal: legal ?? { check: false, fold: false },
      // Timeout/consequence text needs the engine's resolution; the interactive client does not
      // expose it, so we surface a neutral line (the engine still enforces turns over the relay).
      resolution: null,
      decisionMs: ruleset.timeouts.decisionMs,
    });
  }, [state, heroSeat, heroHole, legal, ruleset.timeouts.decisionMs]);

  function requestAction(
    choice: 'fold' | 'check' | 'call' | 'bet' | 'raise',
    amount: number,
  ): void {
    if (!legal) return;
    const action = actionFromChoice(choice, heroSeat, legal, amount);
    pendingAction.current = action;
    const toCall = legal.call ? legal.call.amount : 0;
    setPrompt(signingPromptVM(action, { potBefore: vm?.totalPot ?? 0, toCall }));
  }

  function confirmAction(): void {
    const action = pendingAction.current;
    setPrompt(null);
    pendingAction.current = null;
    if (action) client.submitAction(action);
  }

  function cancelAction(): void {
    setPrompt(null);
    pendingAction.current = null;
  }

  const seatLabel = useMemo(() => networkSeatLabel(seated.players), [seated.players]);

  const showdown = finalState ? showdownViewModel(finalState, startingStacks) : null;
  const settlement = finalState ? settlementViewModel(finalState, startingStacks) : null;

  // Hero's remaining stack to cash back into the wallet on leave (final state if the hand
  // completed, else the live state, else the starting buy-in).
  const heroStack =
    (finalState ?? state)?.seats.find((s) => s.seat === heroSeat)?.stack ??
    (startingStacks.get(heroSeat) ?? 0);

  return (
    <div style={{ maxWidth: 860, margin: '20px auto', padding: 16, display: 'grid', gap: 12 }}>
      <MainnetBanner regtest={ruleset.currency === 'play-regtest'} />

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h2 style={{ margin: 0 }}>
          {props.tableName} — blinds {ruleset.blinds.smallBlind}/{ruleset.blinds.bigBlind}
          {state ? ` (phase ${state.phase})` : ''}
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

      {error && (
        <div role="alert" style={{ color: '#f88' }}>
          Table error: {error}
        </div>
      )}
      {status && !error && <div style={{ color: '#aaa' }}>{status}</div>}

      {vm && (
        <>
          <PokerTable vm={vm} seatLabel={seatLabel} />
          <TimerBanner timer={vm.timer} />

          {!finalState ? (
            update?.yourTurn ? (
              <ActionBar
                vm={vm.actionBar}
                heroSeat={heroSeat}
                betAmount={betAmount}
                onBetAmountChange={setBetAmount}
                onAction={requestAction}
                pot={vm.totalPot}
              />
            ) : (
              <div role="group" aria-label="actions" style={{ color: '#999', padding: 8 }}>
                Waiting for the other player(s)…
              </div>
            )
          ) : (
            <div style={{ display: 'grid', gap: 8 }}>
              {showdown && <ShowdownPanel vm={showdown} />}
              {settlement && <SettlementSummary vm={settlement} />}
              <p style={{ color: '#aaa', fontSize: 13 }}>
                Hand complete. A networked table plays one hand per session (the deck handshake is
                per-hand); return to the lobby to play another.
              </p>
              <button
                type="button"
                onClick={() => props.onLeave(heroStack)}
                style={{ padding: '8px 16px', fontSize: 16 }}
              >
                Cash out &amp; back to lobby
              </button>
            </div>
          )}
        </>
      )}

      <SigningModal prompt={prompt} onConfirm={confirmAction} onCancel={cancelAction} />
    </div>
  );
}
