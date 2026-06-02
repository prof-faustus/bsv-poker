/**
 * Structured, levelled logging (REQ-APP-120) with a hard redaction guarantee: logs, metrics, traces,
 * and diagnostic bundles MUST NOT contain key material (REQ-APP-124). Every record is per-component
 * and levelled; field payloads pass through `redact()`, which strips anything named like a secret
 * (recursively) so a stray private scalar/seed never reaches a sink.
 */

export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

export interface LogRecord {
  readonly ts: number;
  readonly level: LogLevel;
  readonly component: string;
  readonly msg: string;
  readonly fields?: Readonly<Record<string, unknown>>;
}

export interface LogSink {
  write(record: LogRecord): void;
}

const LEVEL_ORDER: Record<LogLevel, number> = { debug: 0, info: 1, warn: 2, error: 3 };

// Field names that carry key material and MUST never be logged in the clear.
const SECRET_NAME = /(priv|secret|seed|mnemonic|scalar|wif|privatekey|password)/i;
export const REDACTED = '[redacted]';

/** Recursively replace any secret-named field with a redaction marker. */
export function redact(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(redact);
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k] = SECRET_NAME.test(k) ? REDACTED : redact(v);
    }
    return out;
  }
  return value;
}

export class Logger {
  private readonly component: string;
  private readonly sink: LogSink;
  private readonly min: LogLevel;

  constructor(component: string, sink: LogSink, min: LogLevel = 'info') {
    this.component = component;
    this.sink = sink;
    this.min = min;
  }

  log(level: LogLevel, msg: string, fields?: Record<string, unknown>): void {
    if (LEVEL_ORDER[level] < LEVEL_ORDER[this.min]) return;
    this.sink.write({
      ts: Date.now(),
      level,
      component: this.component,
      msg,
      fields: fields ? (redact(fields) as Record<string, unknown>) : undefined,
    });
  }

  debug(msg: string, fields?: Record<string, unknown>): void { this.log('debug', msg, fields); }
  info(msg: string, fields?: Record<string, unknown>): void { this.log('info', msg, fields); }
  warn(msg: string, fields?: Record<string, unknown>): void { this.log('warn', msg, fields); }
  error(msg: string, fields?: Record<string, unknown>): void { this.log('error', msg, fields); }
}

/** Collects records in memory — used by tests and diagnostic bundles (which inherit redaction). */
export class MemorySink implements LogSink {
  readonly records: LogRecord[] = [];
  write(record: LogRecord): void { this.records.push(record); }
}
