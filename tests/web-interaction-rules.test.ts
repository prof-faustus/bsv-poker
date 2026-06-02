/**
 * Web-client interaction + persistence rules, enforced as a passing test (REQ-UI-003 / REQ-APP-053:
 * interactions use explicit handlers, never `<form>` submit — avoids webview navigation; REQ-UI-002
 * / REQ-APP-042: `localStorage`/`sessionStorage` MUST NOT hold load-bearing state — keys, table
 * state, transcripts live in IndexedDB). Scans the built `apps/client-web` source.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const WEB_SRC = join(dirname(fileURLToPath(import.meta.url)), '..', 'apps', 'client-web', 'src');

function files(dir: string, out: string[] = []): string[] {
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (statSync(p).isDirectory()) files(p, out);
    else if (/\.(ts|tsx)$/.test(e)) out.push(p);
  }
  return out;
}

// Source with comment-only lines removed, so the rules match real code, not the doc-comments that
// reference them (e.g. "// no <form> submit, REQ-UI-003").
function codeLines(text: string): string[] {
  return text.split('\n').filter((l) => !/^\s*(\*|\/\/|\/\*)/.test(l));
}

test('web client uses explicit handlers — no <form> element and no onSubmit (REQ-UI-003/APP-053)', () => {
  const bad: string[] = [];
  for (const f of files(WEB_SRC)) {
    for (const line of codeLines(readFileSync(f, 'utf8'))) {
      if (/<form[\s>/]/.test(line) || /onSubmit\s*=/.test(line)) bad.push(`${f.slice(WEB_SRC.length + 1)}: ${line.trim()}`);
    }
  }
  assert.deepEqual(bad, [], `web client must use explicit onClick handlers, not <form> submit:\n${bad.join('\n')}`);
});

test('web client never persists LOAD-BEARING state in localStorage/sessionStorage (REQ-UI-002/APP-042)', () => {
  const bad: string[] = [];
  for (const f of files(WEB_SRC)) {
    for (const line of codeLines(readFileSync(f, 'utf8'))) {
      if (/sessionStorage/.test(line)) bad.push(`${f.slice(WEB_SRC.length + 1)}: sessionStorage is not permitted → ${line.trim()}`);
      // localStorage may hold the play-money wallet balance, but NEVER keys/secrets/transcripts/seeds.
      if (/localStorage/.test(line) && /(priv|secret|seed|transcript|mnemonic)/i.test(line)) {
        bad.push(`${f.slice(WEB_SRC.length + 1)}: load-bearing material in localStorage → ${line.trim()}`);
      }
    }
  }
  assert.deepEqual(bad, [], `load-bearing state (keys/transcripts) must live in IndexedDB, never web storage:\n${bad.join('\n')}`);
});
