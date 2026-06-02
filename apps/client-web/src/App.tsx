/**
 * App shell — a small screen state machine for REAL relay-backed multiplayer plus the offline
 * practice flow:
 *
 *   Connect → Lobby → WaitingRoom → NetworkTable      (real multiplayer over the relay)
 *   Connect/Lobby → Practice (local Table vs bot)     (the existing offline engine flow)
 *
 * IMPORTANT (browser-bundle scope): this app imports ONLY the browser-safe workspace packages
 * via ui-core / app-services: protocol-types (pure-TS sha256), hand-eval, engine, the five game
 * modules (via app-services createGameModule), and the relay transport (fetch/SSE in network.ts).
 * It NEVER imports crypto-mentalpoker / script-templates-ts / tx-builder / wallet-custody — those
 * use node:crypto and are the Node SDK path (§A2.3).
 *
 * Wallet: a single WalletService (play-money, localStorage-persisted) owns the player's balance.
 * Join/Create BUY IN for the table's starting stack (blocked with a clear message if the balance
 * is too low); leaving a table CASHES OUT the remaining stack back to the wallet. Funds movement
 * is the local balance now — the live on-chain backend is wired separately on the Node side.
 */
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  LobbyClient,
  LocalTableClient,
  RelayClient,
  WalletService,
  type WalletPersistence,
  type WalletState,
  type OpenTable,
  type SeatedResult,
  type TableMeta,
} from '@bsv-poker/app-services';
import {
  rulesetFromForm,
  generateIdentity,
  buyInCheck,
  type NetworkTableForm,
  type SessionIdentity,
} from '@bsv-poker/ui-core/view-models';
import type { Ruleset } from '@bsv-poker/protocol-types';
import { Connect } from './screens/Connect.tsx';
import { NetworkLobby } from './screens/NetworkLobby.tsx';
import { WaitingRoom } from './screens/WaitingRoom.tsx';
import { NetworkTable } from './screens/NetworkTable.tsx';
import { Lobby } from './screens/Lobby.tsx';
import { Table } from './screens/Table.tsx';
import type { TableCreateForm } from '@bsv-poker/ui-core/view-models';

type Screen =
  | { kind: 'connect' }
  | { kind: 'lobby' }
  | { kind: 'waiting' }
  | { kind: 'networkTable'; tableId: string; tableName: string; seated: SeatedResult }
  | { kind: 'practiceForm' }
  | { kind: 'practiceTable'; client: LocalTableClient; ruleset: Ruleset };

function metaFromForm(form: NetworkTableForm): TableMeta {
  // The form carries the chosen variant + hi-lo; the relay client is variant-generic so the
  // created TableMeta.variant flows straight through to play. (hiLo is carried for display; the
  // app-services rulesetFromMeta currently builds high-only — see NetworkLobby note.)
  return {
    name: form.name.trim(),
    variant: form.variant,
    smallBlind: form.smallBlind,
    bigBlind: form.bigBlind,
    startingStack: form.startingStack,
    maxSeats: form.maxSeats,
    // hiLo is not part of app-services TableMeta's type but survives the relay's JSON round-trip;
    // it is shown in the lobby list. NOTE: app-services rulesetFromMeta currently builds high-only
    // (hiLo:false) on the seated side, so networked Omaha settles high-only until that reads hiLo.
    ...(form.variant === 'omaha' && form.hiLo ? { hiLo: true } : {}),
  } as TableMeta;
}

/** localStorage-backed wallet persistence. A play-money balance MAY use localStorage (the task
 * permits it); load-bearing keys/table state must NOT — and do not — live here. */
const WALLET_KEY = 'bsv-poker.wallet.v1';
const walletPersistence: WalletPersistence = {
  load(): WalletState | null {
    try {
      const raw = globalThis.localStorage?.getItem(WALLET_KEY);
      return raw ? (JSON.parse(raw) as WalletState) : null;
    } catch {
      return null;
    }
  },
  save(state: WalletState): void {
    try {
      globalThis.localStorage?.setItem(WALLET_KEY, JSON.stringify(state));
    } catch {
      /* storage unavailable — wallet still works in-memory for the session */
    }
  },
};

export function App(): React.JSX.Element {
  const identity = useMemo<SessionIdentity>(() => generateIdentity(), []);
  const wallet = useMemo(() => new WalletService({ persistence: walletPersistence }), []);

  // Re-render on any wallet change so balance/history stay live across the app.
  const [walletState, setWalletState] = useState<WalletState>(() => wallet.state());
  useEffect(() => wallet.onChange(setWalletState), [wallet]);

  // Connecting to the relay is automatic plumbing — NOT a user action. The bundled-local relay
  // (loopback) is reached on launch and the player lands straight in the lobby.
  const [screen, setScreen] = useState<Screen>({ kind: 'lobby' });
  const [relay, setRelay] = useState('http://localhost:8091');
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState<string | null>(null);
  const lobbyRef = useRef<LobbyClient | null>(null);
  if (lobbyRef.current === null) lobbyRef.current = new LobbyClient(new RelayClient(relay));

  // Track the active buy-in (table id + amount) so leaving/cancelling cashes the right table.
  const activeTableId = useRef<string | null>(null);
  const activeBuyIn = useRef<number>(0);

  // Waiting-room state (lives in App so it survives across renders while the join is in flight).
  const [waitName, setWaitName] = useState('');
  const [waitCapacity, setWaitCapacity] = useState(2);
  const [waitPlayers, setWaitPlayers] = useState<{ id: string; pub: string }[]>([]);
  const [waitError, setWaitError] = useState<string | null>(null);
  const abortRef = useRef<(() => void) | null>(null);

  const connect = useCallback(
    async (base: string): Promise<void> => {
      setConnecting(true);
      setConnectError(null);
      const lobby = new LobbyClient(new RelayClient(base));
      try {
        // listTables doubles as a connectivity check (CORS / relay reachable).
        await lobby.listTables();
        lobbyRef.current = lobby;
        setRelay(base);
        setScreen({ kind: 'lobby' });
      } catch (e) {
        setConnectError(`Could not reach relay: ${(e as Error).message}`);
      } finally {
        setConnecting(false);
      }
    },
    [],
  );

  const enterWaitingRoom = useCallback(
    (tableId: string, meta: TableMeta): void => {
      const lobby = lobbyRef.current;
      if (!lobby) return;
      // Buy in for the table's starting stack — block (with a clear message) if too low.
      const check = buyInCheck(wallet.getBalance(), meta.startingStack);
      if (!check.canAfford) {
        setConnectError(check.message);
        return;
      }
      wallet.buyIn(meta.startingStack, tableId);
      activeTableId.current = tableId;
      activeBuyIn.current = meta.startingStack;

      setWaitName(meta.name);
      setWaitCapacity(meta.maxSeats);
      setWaitPlayers([{ id: identity.id, pub: identity.pub }]);
      setWaitError(null);
      setConnectError(null);
      setScreen({ kind: 'waiting' });

      const { seated, abort } = lobby.joinWaitingRoom(
        tableId,
        { id: identity.id, pub: identity.pub },
        meta,
        (players) => setWaitPlayers([...players]),
      );
      abortRef.current = abort;
      seated.then(
        (result) => {
          abortRef.current = null;
          setScreen({ kind: 'networkTable', tableId, tableName: meta.name, seated: result });
        },
        (e) => setWaitError((e as Error).message),
      );
    },
    [identity, wallet],
  );

  const createTable = useCallback(
    async (form: NetworkTableForm): Promise<void> => {
      const lobby = lobbyRef.current;
      if (!lobby) return;
      const meta = metaFromForm(form);
      // Pre-check affordability before we create a table on the relay.
      const check = buyInCheck(wallet.getBalance(), meta.startingStack);
      if (!check.canAfford) {
        setConnectError(check.message);
        return;
      }
      try {
        const tableId = await lobby.createTable(meta);
        enterWaitingRoom(tableId, meta);
      } catch (e) {
        setConnectError(`Create failed: ${(e as Error).message}`);
      }
    },
    [enterWaitingRoom, wallet],
  );

  const joinTable = useCallback(
    (table: OpenTable): void => {
      enterWaitingRoom(table.id, table.meta);
    },
    [enterWaitingRoom],
  );

  /** Cash the hero's remaining stack back to the wallet for the active table, then go to lobby. */
  const cashOutAndLeave = useCallback(
    (heroStack: number): void => {
      const id = activeTableId.current;
      wallet.cashOut(Math.max(0, Math.floor(heroStack)), id ?? undefined);
      activeTableId.current = null;
      activeBuyIn.current = 0;
      setScreen(lobbyRef.current ? { kind: 'lobby' } : { kind: 'connect' });
    },
    [wallet],
  );

  const cancelWaiting = useCallback((): void => {
    abortRef.current?.();
    abortRef.current = null;
    // We never sat down — refund the full buy-in back to the wallet.
    wallet.cashOut(activeBuyIn.current, activeTableId.current ?? undefined);
    activeTableId.current = null;
    activeBuyIn.current = 0;
    setScreen({ kind: 'lobby' });
  }, [wallet]);

  function startPractice(form: TableCreateForm, botOpponents: number): void {
    const ruleset = rulesetFromForm(form);
    // Buy in for the practice table too (blocked if balance too low).
    const check = buyInCheck(wallet.getBalance(), ruleset.minBuyIn);
    if (!check.canAfford) {
      setConnectError(check.message);
      setScreen({ kind: lobbyRef.current ? 'lobby' : 'connect' });
      return;
    }
    wallet.buyIn(ruleset.minBuyIn, 'practice');
    activeTableId.current = 'practice';
    activeBuyIn.current = ruleset.minBuyIn;
    // The human chose how many bots; they always play seat 0, bots fill the rest.
    const seatCount = Math.max(2, Math.min(9, botOpponents + 1));
    const client = new LocalTableClient({ ruleset, heroSeat: 0, seatCount });
    setScreen({ kind: 'practiceTable', client, ruleset });
  }

  switch (screen.kind) {
    case 'connect':
      return (
        <Connect
          defaultRelay={relay}
          identityId={identity.id}
          connecting={connecting}
          error={connectError}
          onConnect={(base) => void connect(base)}
          onPractice={() => setScreen({ kind: 'practiceForm' })}
        />
      );

    case 'lobby':
      return (
        <NetworkLobby
          lobby={lobbyRef.current!}
          relay={relay}
          identityId={identity.id}
          wallet={wallet}
          walletState={walletState}
          createError={connectError}
          onCreate={(form) => void createTable(form)}
          onJoin={joinTable}
          onPractice={() => setScreen({ kind: 'practiceForm' })}
          onDisconnect={() => {
            lobbyRef.current = null;
            setScreen({ kind: 'connect' });
          }}
        />
      );

    case 'waiting':
      return (
        <WaitingRoom
          tableName={waitName}
          capacity={waitCapacity}
          players={waitPlayers}
          myId={identity.id}
          error={waitError}
          onCancel={cancelWaiting}
        />
      );

    case 'networkTable':
      return (
        <NetworkTable
          relay={relay}
          tableId={screen.tableId}
          tableName={screen.tableName}
          seated={screen.seated}
          wallet={wallet}
          walletState={walletState}
          onLeave={cashOutAndLeave}
        />
      );

    case 'practiceForm':
      return (
        <Lobby
          wallet={wallet}
          walletState={walletState}
          onStart={startPractice}
          onBack={() => setScreen(lobbyRef.current ? { kind: 'lobby' } : { kind: 'connect' })}
        />
      );

    case 'practiceTable':
      return (
        <Table
          client={screen.client}
          ruleset={screen.ruleset}
          wallet={wallet}
          walletState={walletState}
          onLeave={cashOutAndLeave}
        />
      );
  }
}
