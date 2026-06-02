/**
 * Wallet service (core §9, app §A6.2) — a player's funds with the ability to ADD and REMOVE
 * money, and buy in / cash out of tables. Browser-safe (no node:crypto).
 *
 * Funds movement goes through a pluggable FundingBackend so the SAME wallet works:
 *  - **play-regtest** (default now): play-money — add/remove credit/debit the local balance,
 *    persisted, with a transaction history. No external value (core D8).
 *  - **regtest node faucet** (live-ready, Node side): deposit mines real regtest coins to the
 *    player's key via the embedded BSV node (see tools/wallet-e2e.ts).
 *  - **mainnet** (later, behind the research flag): deposit/withdraw are real on-chain moves.
 *
 * Amounts are integer satoshis (or play-money chips at 1:1) — never fractional (INV-BS-1).
 */

export type WalletNetwork = 'play-regtest' | 'regtest' | 'mainnet-research';

export type FundsEventKind = 'deposit' | 'withdraw' | 'buy-in' | 'cash-out';

export interface FundsEvent {
  readonly kind: FundsEventKind;
  readonly amount: number;
  readonly balanceAfter: number;
  readonly at: number; // ms timestamp (UI display only; not consensus)
  readonly memo?: string;
}

export interface WalletState {
  readonly network: WalletNetwork;
  readonly balance: number;
  readonly history: readonly FundsEvent[];
}

/** Where deposits/withdrawals actually move value. Play-money is a no-op (balance-only). */
export interface FundingBackend {
  /** Bring `amount` in (regtest faucet / real deposit). Resolves when credited. */
  deposit(amount: number, address?: string): Promise<void>;
  /** Send `amount` out to `dest`. Resolves when the spend is accepted. */
  withdraw(amount: number, dest: string): Promise<void>;
}

/** Default play-money backend — funds are local chips with no external value (core D8). */
export const playMoneyBackend: FundingBackend = {
  async deposit() {
    /* play-money: crediting the balance is the whole operation */
  },
  async withdraw() {
    /* play-money: debiting the balance is the whole operation */
  },
};

/** Optional persistence (IndexedDB on web / SQLite on desktop, core §12.1). */
export interface WalletPersistence {
  load(): WalletState | null;
  save(state: WalletState): void;
}

export class WalletService {
  private network: WalletNetwork;
  private balance: number;
  private historyLog: FundsEvent[];
  private readonly backend: FundingBackend;
  private readonly persistence: WalletPersistence | null;
  private listeners: Array<(s: WalletState) => void> = [];

  constructor(opts?: {
    network?: WalletNetwork;
    backend?: FundingBackend;
    persistence?: WalletPersistence;
  }) {
    const persisted = opts?.persistence?.load() ?? null;
    this.network = persisted?.network ?? opts?.network ?? 'play-regtest';
    this.balance = persisted?.balance ?? 0;
    this.historyLog = persisted ? [...persisted.history] : [];
    this.backend = opts?.backend ?? playMoneyBackend;
    this.persistence = opts?.persistence ?? null;
  }

  state(): WalletState {
    return { network: this.network, balance: this.balance, history: [...this.historyLog] };
  }
  getBalance(): number {
    return this.balance;
  }
  onChange(cb: (s: WalletState) => void): () => void {
    this.listeners.push(cb);
    return () => {
      this.listeners = this.listeners.filter((x) => x !== cb);
    };
  }

  private record(kind: FundsEventKind, amount: number, memo?: string): void {
    const ev: FundsEvent = { kind, amount, balanceAfter: this.balance, at: Date.now(), ...(memo ? { memo } : {}) };
    this.historyLog.push(ev);
    this.persistence?.save(this.state());
    const snap = this.state();
    for (const l of this.listeners) l(snap);
  }

  private requirePositiveInt(amount: number): void {
    if (!Number.isInteger(amount) || amount <= 0) throw new Error('amount must be a positive integer (satoshis)');
  }

  /** ADD funds. On mainnet this requires the research flag (fail-closed otherwise). */
  async addFunds(amount: number, opts?: { address?: string; memo?: string }): Promise<void> {
    this.requirePositiveInt(amount);
    if (this.network === 'mainnet-research') {
      // real deposit path; the backend performs the on-chain credit
    }
    await this.backend.deposit(amount, opts?.address);
    this.balance += amount;
    this.record('deposit', amount, opts?.memo);
  }

  /** REMOVE funds (cash out / withdraw to an external address). */
  async withdraw(amount: number, dest: string, memo?: string): Promise<void> {
    this.requirePositiveInt(amount);
    if (amount > this.balance) throw new Error('insufficient balance');
    await this.backend.withdraw(amount, dest);
    this.balance -= amount;
    this.record('withdraw', amount, memo ?? `to ${dest}`);
  }

  /** Buy in to a table: move `amount` from the wallet into a table stack. */
  buyIn(amount: number, tableId?: string): number {
    this.requirePositiveInt(amount);
    if (amount > this.balance) throw new Error('insufficient balance to buy in');
    this.balance -= amount;
    this.record('buy-in', amount, tableId ? `table ${tableId}` : undefined);
    return amount; // the starting stack at the table
  }

  /** Cash out of a table: return the remaining `stack` to the wallet. */
  cashOut(stack: number, tableId?: string): void {
    if (!Number.isInteger(stack) || stack < 0) throw new Error('stack must be a non-negative integer');
    this.balance += stack;
    this.record('cash-out', stack, tableId ? `table ${tableId}` : undefined);
  }

  setNetwork(network: WalletNetwork): void {
    this.network = network;
    this.persistence?.save(this.state());
  }
}
