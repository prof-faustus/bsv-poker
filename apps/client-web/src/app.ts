/**
 * App controller — framework-free (vanilla DOM). A small screen state machine for REAL relay-backed
 * multiplayer plus the practice flow, ported off React to the in-tree `mount`/store model:
 *
 *   Connect → Lobby → WaitingRoom → NetworkTable      (real multiplayer over the relay)
 *
 * The controller owns ALL mutable state (model) and the long-lived table-session lifecycle (the
 * InteractiveNetworkedTableClient, its onUpdate subscription, and teardown). The screen modules are
 * PURE renders of a model slice + explicit handlers; `mount` re-renders the whole subtree on every
 * `notify()` and preserves input focus/caret, so controlled form fields behave with no diffing.
 *
 * IMPORTANT (browser-bundle scope): this app imports ONLY the browser-safe workspace packages via
 * ui-core / app-services: protocol-types (pure-TS sha256), hand-eval, engine, the five game modules
 * (via app-services createGameModule), and the relay transport (fetch/SSE). It NEVER imports
 * crypto-mentalpoker / script-templates-ts / tx-builder / wallet-custody — those use node:crypto and
 * are the Node SDK path (§A2.3).
 *
 * Wallet: a single WalletService (play-money, localStorage-persisted) owns the player's balance.
 * Join/Create BUY IN for the table's starting stack (blocked with a clear message if too low);
 * leaving a table CASHES OUT the remaining stack back to the wallet.
 */
import { el, mount } from '@bsv-poker/ui-core/dom';
import {
  LobbyClient,
  RelayClient,
  WalletService,
  InteractiveNetworkedTableClient,
  sessionAuthFromSeed,
  deriveSeatSeed,
  type WalletPersistence,
  type WalletState,
  type OpenTable,
  type SeatedResult,
  type TableMeta,
  type ClientUpdate,
  type SessionAuth,
} from '@bsv-poker/app-services';
import {
  generateIdentity,
  buyInCheck,
  tableViewModel,
  showdownViewModel,
  settlementViewModel,
  signingPromptVM,
  actionFromChoice,
  networkSeatLabel,
  VARIANT_SEAT_RANGE,
  type NetworkTableForm,
  type SessionIdentity,
  type SeatVM,
  type SigningPromptVM,
} from '@bsv-poker/ui-core/view-models';
import type { HoldemState } from '@bsv-poker/game-holdem';
import type { Action } from '@bsv-poker/protocol-types';
import { connectScreen } from './screens/connect.ts';
import { networkLobbyScreen } from './screens/network-lobby.ts';
import { waitingRoomScreen } from './screens/waiting-room.ts';
import { networkTableScreen } from './screens/network-table.ts';

type Screen =
  | { kind: 'connect' }
  | { kind: 'lobby' }
  | { kind: 'waiting' }
  | { kind: 'networkTable'; tableId: string; tableName: string; seated: SeatedResult };

/** localStorage-backed wallet persistence. A play-money balance MAY use localStorage; load-bearing
 *  keys/table state must NOT — and do not — live here. */
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

function metaFromForm(form: NetworkTableForm): TableMeta {
  return {
    name: form.name.trim(),
    variant: form.variant,
    smallBlind: form.smallBlind,
    bigBlind: form.bigBlind,
    startingStack: form.startingStack,
    maxSeats: form.maxSeats,
    ...(form.variant === 'omaha' && form.hiLo ? { hiLo: true } : {}),
  } as TableMeta;
}

/** The live table session: the interactive client plus the reactive values its stream drives. */
interface TableSession {
  readonly client: InteractiveNetworkedTableClient;
  off: () => void;
  cancelled: boolean;
  readonly heroSeat: number;
  readonly ruleset: SeatedResult['ruleset'];
  readonly startingStacks: Map<number, number>;
  readonly seatLabel: (seat: SeatVM) => string;
  update: ClientUpdate | null;
  finalState: HoldemState | null;
  status: string;
  error: string | null;
  prompt: SigningPromptVM | null;
  pendingAction: Action | null;
  betAmount: number;
  addAmount: number;
  withdrawAmount: number;
  withdrawDest: string;
}

/**
 * Build the app: returns the root element to mount into the page. Side effects (relay polling, the
 * session client) are owned here and torn down on screen transitions.
 */
export function createApp(): HTMLElement {
  const root = el('div', {});

  // ---- immutable session-identity / services ----
  const identity: SessionIdentity = generateIdentity();
  const wallet = new WalletService({ persistence: walletPersistence });

  // Session signing key (Ed25519) — signs every relay envelope so peers can prove who sent it
  // (audit 1–3). Derived from a fresh wallet root; the identity pub used for seating IS this key.
  let auth: SessionAuth | null = null;
  {
    const seed = new Uint8Array(32);
    (globalThis.crypto as Crypto).getRandomValues(seed);
    void sessionAuthFromSeed(deriveSeatSeed(seed, 'bsv-poker/seat-ed25519')).then((a) => {
      auth = a;
    });
  }

  // ---- mutable model ----
  const model = {
    screen: { kind: 'lobby' } as Screen,
    // The browser talks ONLY to the player's OWN local node (tools/local-node.ts) on loopback — NOT a
    // central relay (there is none). The local node bridges HTTP/SSE ↔ the P2P mesh. Default 8090.
    relay: 'http://127.0.0.1:8090',
    relayInput: 'http://127.0.0.1:8090',
    connecting: false,
    connectError: null as string | null,
    lobby: new LobbyClient(new RelayClient('http://127.0.0.1:8090')) as LobbyClient | null,

    activeTableId: null as string | null,
    activeBuyIn: 0,

    // waiting room
    waitName: '',
    waitCapacity: 2,
    waitPlayers: [] as { id: string; pub: string }[],
    waitError: null as string | null,
    abort: null as (() => void) | null,

    // lobby screen
    tables: [] as OpenTable[],
    loadError: null as string | null,
    lobbyForm: { name: 'Friday night', variant: 'holdem', hiLo: false, smallBlind: 1, bigBlind: 2, startingStack: 100, maxSeats: 2 } as NetworkTableForm,
    lobbyAddAmount: 100,
    lobbyWithdrawAmount: 0,
    lobbyWithdrawDest: '',

    // table
    session: null as TableSession | null,
  };

  // ---- store ----
  const listeners = new Set<() => void>();
  const store = { subscribe(l: () => void): () => void { listeners.add(l); return () => listeners.delete(l); } };
  const notify = (): void => { for (const l of [...listeners]) l(); };

  // Re-render on any wallet change so balance/history stay live across the app.
  wallet.onChange(notify);

  // ---- lobby polling (active only while on the lobby screen) ----
  const refresh = async (): Promise<void> => {
    const lobby = model.lobby;
    if (!lobby) return;
    try {
      const list = await lobby.listTables();
      model.tables = list;
      model.loadError = null;
    } catch (e) {
      model.loadError = (e as Error).message;
    }
    notify();
  };
  setInterval(() => { if (model.screen.kind === 'lobby') void refresh(); }, 3000);

  // ---- transitions / handlers ----
  function connect(base: string): void {
    model.connecting = true;
    model.connectError = null;
    notify();
    const lobby = new LobbyClient(new RelayClient(base));
    // listTables doubles as a connectivity check (is the player's own local node reachable?).
    lobby.listTables().then(
      () => {
        model.lobby = lobby;
        model.relay = base;
        model.screen = { kind: 'lobby' };
        model.connecting = false;
        void refresh();
        notify();
      },
      (e: unknown) => {
        model.connectError = `Could not reach your local node: ${(e as Error).message}`;
        model.connecting = false;
        notify();
      },
    );
  }

  function enterWaitingRoom(tableId: string, meta: TableMeta): void {
    const lobby = model.lobby;
    if (!lobby) return;
    const check = buyInCheck(wallet.getBalance(), meta.startingStack);
    if (!check.canAfford) {
      model.connectError = check.message;
      notify();
      return;
    }
    const a = auth;
    if (!a) {
      model.waitError = 'session key not ready — try again in a moment';
      notify();
      return;
    }
    wallet.buyIn(meta.startingStack, tableId);
    model.activeTableId = tableId;
    model.activeBuyIn = meta.startingStack;

    model.waitName = meta.name;
    model.waitCapacity = meta.maxSeats;
    model.waitPlayers = [{ id: identity.id, pub: a.pub }];
    model.waitError = null;
    model.connectError = null;
    model.screen = { kind: 'waiting' };
    notify();

    const { seated, abort } = lobby.joinWaitingRoom(
      tableId,
      { id: identity.id, pub: a.pub, sign: (m) => a.sign(m) },
      meta,
      (players) => { model.waitPlayers = [...players]; notify(); },
    );
    model.abort = abort;
    seated.then(
      (result) => {
        model.abort = null;
        startNetworkTable(tableId, meta.name, result);
      },
      (e: unknown) => { model.waitError = (e as Error).message; notify(); },
    );
  }

  function createTable(form: NetworkTableForm): void {
    const lobby = model.lobby;
    if (!lobby) return;
    const meta = metaFromForm(form);
    const check = buyInCheck(wallet.getBalance(), meta.startingStack);
    if (!check.canAfford) {
      model.connectError = check.message;
      notify();
      return;
    }
    lobby.createTable(meta).then(
      (tableId) => enterWaitingRoom(tableId, meta),
      (e: unknown) => { model.connectError = `Create failed: ${(e as Error).message}`; notify(); },
    );
  }

  function startNetworkTable(tableId: string, tableName: string, seated: SeatedResult): void {
    const entropy = new Uint8Array(32);
    (globalThis.crypto as Crypto).getRandomValues(entropy);
    const client = new InteractiveNetworkedTableClient({
      relay: new RelayClient(model.relay),
      tableId,
      mySeat: seated.mySeat,
      seats: seated.seats,
      ruleset: seated.ruleset,
      entropy,
      ...(auth ? { auth } : {}),
      seatPubs: seated.players.map((pl) => pl.pub),
    });
    const session: TableSession = {
      client,
      off: () => {},
      cancelled: false,
      heroSeat: seated.mySeat,
      ruleset: seated.ruleset,
      startingStacks: new Map(seated.seats.map((s) => [s.seat, s.stack])),
      seatLabel: networkSeatLabel(seated.players),
      update: null,
      finalState: null,
      status: 'Agreeing the deck (commit/reveal handshake)…',
      error: null,
      prompt: null,
      pendingAction: null,
      betAmount: seated.ruleset.blinds.bigBlind,
      addAmount: 100,
      withdrawAmount: 0,
      withdrawDest: '',
    };
    model.session = session;
    model.screen = { kind: 'networkTable', tableId, tableName, seated };

    session.off = client.onUpdate((u) => {
      session.update = u;
      if (!u.complete) session.status = '';
      notify();
    });
    // A continuous table: play hand after hand until the player leaves (client.abort()).
    client.playSession({ maxHands: 100 }).then(
      () => {
        const s = client.getState();
        if (!session.cancelled && s) { session.finalState = s as HoldemState; notify(); }
      },
      (e: unknown) => { if (!session.cancelled) { session.error = (e as Error).message; notify(); } },
    );
    notify();
  }

  function teardownSession(): void {
    const s = model.session;
    if (!s) return;
    s.cancelled = true;
    s.client.abort();
    s.off();
    model.session = null;
  }

  function cashOutAndLeave(heroStack: number): void {
    teardownSession();
    wallet.cashOut(Math.max(0, Math.floor(heroStack)), model.activeTableId ?? undefined);
    model.activeTableId = null;
    model.activeBuyIn = 0;
    model.screen = model.lobby ? { kind: 'lobby' } : { kind: 'connect' };
    void refresh();
    notify();
  }

  function cancelWaiting(): void {
    model.abort?.();
    model.abort = null;
    // We never sat down — refund the full buy-in back to the wallet.
    wallet.cashOut(model.activeBuyIn, model.activeTableId ?? undefined);
    model.activeTableId = null;
    model.activeBuyIn = 0;
    model.screen = { kind: 'lobby' };
    void refresh();
    notify();
  }

  // ---- table action handlers ----
  function requestAction(choice: 'fold' | 'check' | 'call' | 'bet' | 'raise', amount: number): void {
    const s = model.session;
    if (!s || !s.update?.yourTurn) return;
    const legal = s.client.legalActions();
    if (!legal) return;
    const action = actionFromChoice(choice, s.heroSeat, legal, amount);
    s.pendingAction = action;
    const toCall = legal.call ? legal.call.amount : 0;
    const state = (s.update.state ?? null) as HoldemState | null;
    const potBefore = state ? tableViewModel({ state, heroSeat: s.heroSeat, heroHole: [], legal, resolution: null, decisionMs: s.ruleset.timeouts.decisionMs }).totalPot : 0;
    s.prompt = signingPromptVM(action, { potBefore, toCall });
    notify();
  }

  function confirmAction(): void {
    const s = model.session;
    if (!s) return;
    const action = s.pendingAction;
    s.prompt = null;
    s.pendingAction = null;
    if (action) s.client.submitAction(action);
    notify();
  }

  function cancelAction(): void {
    const s = model.session;
    if (!s) return;
    s.prompt = null;
    s.pendingAction = null;
    notify();
  }

  // ---- render ----
  function render(): HTMLElement {
    const walletState = wallet.state();
    switch (model.screen.kind) {
      case 'connect':
        return connectScreen({
          defaultRelay: model.relay,
          relay: model.relayInput,
          onRelayChange: (v) => { model.relayInput = v; notify(); },
          identityId: identity.id,
          connecting: model.connecting,
          error: model.connectError,
          onConnect: (base) => connect(base),
        });

      case 'lobby':
        return networkLobbyScreen({
          relay: model.relay,
          identityId: identity.id,
          walletState,
          createError: model.connectError,
          tables: model.tables,
          loadError: model.loadError,
          onRefresh: () => void refresh(),
          form: model.lobbyForm,
          onFieldChange: (key, value) => {
            model.lobbyForm = { ...model.lobbyForm, [key]: value };
            // Clamp seats into the chosen variant's range when the variant changes.
            if (key === 'variant') {
              const r = VARIANT_SEAT_BOUNDS(model.lobbyForm.variant);
              model.lobbyForm = { ...model.lobbyForm, maxSeats: Math.min(Math.max(model.lobbyForm.maxSeats, r.min), r.max) };
            }
            notify();
          },
          addAmount: model.lobbyAddAmount,
          onAddAmountChange: (n) => { model.lobbyAddAmount = n; notify(); },
          onAddFunds: (amount) => void wallet.addFunds(amount),
          withdrawAmount: model.lobbyWithdrawAmount,
          onWithdrawAmountChange: (n) => { model.lobbyWithdrawAmount = n; notify(); },
          withdrawDest: model.lobbyWithdrawDest,
          onWithdrawDestChange: (s) => { model.lobbyWithdrawDest = s; notify(); },
          onWithdraw: (amount, dest) => void wallet.withdraw(amount, dest),
          onCreate: (form) => createTable(form),
          onJoin: (table) => enterWaitingRoom(table.id, table.meta),
          onDisconnect: () => { model.lobby = null; model.screen = { kind: 'connect' }; model.relayInput = model.relay; notify(); },
        });

      case 'waiting':
        return waitingRoomScreen({
          tableName: model.waitName,
          capacity: model.waitCapacity,
          players: model.waitPlayers,
          myId: identity.id,
          error: model.waitError,
          onCancel: cancelWaiting,
        });

      case 'networkTable': {
        const s = model.session;
        if (!s) return el('div', {}, 'Loading…');
        const state = (s.update?.state ?? null) as HoldemState | null;
        const heroHole = state ? (state.hole?.[s.heroSeat] ?? []) : [];
        const legal = s.update?.yourTurn ? s.client.legalActions() : null;
        const vm = state
          ? tableViewModel({
              state, heroSeat: s.heroSeat, heroHole,
              legal: legal ?? { check: false, fold: false },
              resolution: null, decisionMs: s.ruleset.timeouts.decisionMs,
            })
          : null;
        const showdown = s.finalState ? showdownViewModel(s.finalState, s.startingStacks) : null;
        const settlement = s.finalState ? settlementViewModel(s.finalState, s.startingStacks) : null;
        const heroStack =
          (s.finalState ?? state)?.seats.find((x) => x.seat === s.heroSeat)?.stack ??
          (s.startingStacks.get(s.heroSeat) ?? 0);

        return networkTableScreen({
          tableName: model.screen.tableName,
          smallBlind: s.ruleset.blinds.smallBlind,
          bigBlind: s.ruleset.blinds.bigBlind,
          regtest: s.ruleset.currency === 'play-regtest',
          phase: state ? String(state.phase) : null,
          vm,
          status: s.status,
          error: s.error,
          yourTurn: s.update?.yourTurn ?? false,
          handComplete: s.finalState !== null,
          showdown,
          settlement,
          seatLabel: s.seatLabel,
          betAmount: s.betAmount,
          onBetAmountChange: (n) => { s.betAmount = n; notify(); },
          onAction: requestAction,
          prompt: s.prompt,
          onConfirm: confirmAction,
          onCancel: cancelAction,
          walletState,
          addAmount: s.addAmount,
          onAddAmountChange: (n) => { s.addAmount = n; notify(); },
          onAddFunds: (amount) => void wallet.addFunds(amount),
          withdrawAmount: s.withdrawAmount,
          onWithdrawAmountChange: (n) => { s.withdrawAmount = n; notify(); },
          withdrawDest: s.withdrawDest,
          onWithdrawDestChange: (str) => { s.withdrawDest = str; notify(); },
          onWithdraw: (amount, dest) => void wallet.withdraw(amount, dest),
          heroStack,
          onLeave: cashOutAndLeave,
        });
      }
    }
  }

  mount(root, render, store);
  return root;
}

/** Seat bounds for a variant, read from the view-model's VARIANT_SEAT_RANGE (kept local to avoid a
 *  second import path in render). */
function VARIANT_SEAT_BOUNDS(variant: NetworkTableForm['variant']): { min: number; max: number } {
  const r = VARIANT_SEAT_RANGE[variant];
  return { min: r.minSeats, max: r.maxSeats };
}
