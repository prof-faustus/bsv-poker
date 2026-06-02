/**
 * Traceability map (REQ-ENG-003): every requirement the Phase-0/1 build SATISFIES is mapped to
 * its satisfying source file(s) and passing test(s). Requirements not yet implemented are
 * intentionally absent and reported as "planned (later phase)" — an honest matrix, never a
 * claim of completeness (P8). The PHASE01_REQUIRED set is the gate: each MUST be traced.
 */

export interface Trace {
  readonly files: readonly string[];
  readonly tests: readonly string[];
}

export const TRACE_MAP: Readonly<Record<string, Trace>> = {
  'REQ-ARCH-001': { files: ['packages/engine/src/fsm.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-ARCH-002': { files: ['packages/game-holdem/src/holdem.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-ARCH-003': { files: ['packages/game-holdem/src/holdem.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-POKER-001': { files: ['packages/protocol-types/src/cards.ts'], tests: ['packages/protocol-types/test/cards.test.ts'] },
  'REQ-POKER-002': { files: ['packages/protocol-types/src/serialize.ts'], tests: ['packages/protocol-types/test/serialize.test.ts'] },
  'REQ-POKER-003': { files: ['packages/hand-eval/src/high.ts', 'packages/hand-eval/src/low.ts'], tests: ['packages/hand-eval/test/vectors.test.ts'] },
  'REQ-POKER-004': { files: ['packages/hand-eval/src/high.ts'], tests: ['packages/hand-eval/test/vectors.test.ts'] },
  'REQ-POKER-005': { files: ['packages/hand-eval/src/high.ts'], tests: ['packages/hand-eval/test/vectors.test.ts'] },
  'REQ-POKER-006': { files: ['packages/hand-eval/src/low.ts'], tests: ['packages/hand-eval/test/vectors.test.ts'] },
  'REQ-POKER-007': { files: ['packages/hand-eval/src/high.ts'], tests: ['packages/hand-eval/test/vectors.test.ts'] },
  'REQ-POKER-008': { files: ['packages/engine/src/betting.ts'], tests: ['packages/engine/test/betting.test.ts'] },
  'REQ-POKER-009': { files: ['packages/engine/src/betting.ts'], tests: ['packages/engine/test/betting.test.ts'] },
  'REQ-POKER-010': { files: ['packages/engine/src/betting.ts'], tests: ['packages/engine/test/betting.test.ts'] },
  'REQ-POKER-011': { files: ['packages/engine/src/pots.ts'], tests: ['packages/engine/test/pots.test.ts'] },
  'REQ-POKER-012': { files: ['packages/engine/src/pots.ts'], tests: ['packages/engine/test/pots.test.ts'] },
  'REQ-POKER-013': { files: ['packages/engine/src/pots.ts'], tests: ['packages/engine/test/pots.test.ts'] },
  'REQ-FSM-001': { files: ['packages/engine/src/fsm.ts', 'packages/game-holdem/src/holdem.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-FSM-002': { files: ['packages/game-holdem/src/holdem.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-CRYPTO-001': { files: ['packages/crypto-mentalpoker/src/realct.ts'], tests: ['packages/crypto-mentalpoker/test/realct.test.ts'] },
  'REQ-CRYPTO-002': { files: ['packages/crypto-mentalpoker/src/realct.ts'], tests: ['packages/adapters/test/conformance.test.ts'] },
  'REQ-CRYPTO-003': { files: ['packages/crypto-mentalpoker/src/realct.ts'], tests: ['packages/crypto-mentalpoker/test/realct.test.ts'] },
  'REQ-DEP-001': { files: ['packages/adapters/src/contracts.ts'], tests: ['packages/adapters/test/conformance.test.ts'] },
  'REQ-DEP-002': { files: ['packages/adapters/src/contracts.ts'], tests: ['packages/adapters/test/conformance.test.ts'] },
  'REQ-DEP-003': { files: ['packages/adapters/src/conformance.ts'], tests: ['packages/adapters/test/conformance.test.ts', 'packages/crypto-mentalpoker/test/realct.test.ts'] },
  'REQ-DEP-004': { files: ['packages/crypto-mentalpoker/src/realct.ts'], tests: ['packages/crypto-mentalpoker/test/realct.test.ts'] },
  'REQ-TX-001': { files: ['packages/script-templates-ts/src/interpreter.ts'], tests: ['packages/script-templates-ts/test/templates.test.ts'] },
  'REQ-TX-002': { files: ['packages/tx-builder/src/txbuilder.ts'], tests: ['packages/tx-builder/test/txbuilder.test.ts'] },
  'REQ-TX-004': { files: ['packages/script-templates-ts/src/opcodes.ts'], tests: ['packages/script-templates-ts/test/templates.test.ts'] },
  'REQ-TX-005': { files: ['packages/script-templates-ts/src/templates.ts'], tests: ['packages/tx-builder/test/txbuilder.test.ts'] },
  'REQ-TX-010': { files: ['packages/script-templates-ts/src/templates.ts', 'tools/lint-opreturn.ts'], tests: ['packages/script-templates-ts/test/templates.test.ts'] },
  'REQ-TX-011': { files: ['packages/script-templates-ts/src/script.ts'], tests: ['packages/script-templates-ts/test/templates.test.ts'] },
  'REQ-NET-001': { files: ['apps/relay-go/relay/server.go'], tests: ['apps/relay-go/relay/relay_test.go'] },
  'REQ-NET-004': { files: ['apps/indexer-go/indexer/indexer.go'], tests: ['apps/indexer-go/indexer/indexer_test.go'] },
  'REQ-NET-006': { files: ['apps/indexer-go/indexer/indexer.go'], tests: ['apps/indexer-go/indexer/indexer_test.go'] },
  'REQ-NET-007': { files: ['apps/indexer-go/indexer/indexer.go'], tests: ['apps/indexer-go/indexer/indexer_test.go'] },
  'REQ-DATA-002': { files: ['packages/protocol-types/src/tx.ts', 'packages/engine/src/fsm.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-DATA-003': { files: ['packages/game-holdem/src/holdem.ts'], tests: ['packages/game-holdem/test/holdem.test.ts'] },
  'REQ-ENG-002': { files: ['tools/extract-requirements.ts'], tests: ['tools/reproduce.ts'] },
  'REQ-ENG-003': { files: ['tools/traceability.ts', 'tools/trace-map.ts'], tests: ['tools/traceability.ts'] },
  'REQ-TEST-005': { files: ['tools/reproduce.ts'], tests: ['tools/reproduce.ts'] },
  'REQ-BUILD-001': { files: ['pnpm-workspace.yaml'], tests: ['tools/ci.ts'] },
  'REQ-BUILD-003': { files: ['tools/ci.ts'], tests: ['tools/ci.ts'] },
};

/** The Phase-0/1 gate: every id here MUST be traced (REQ-TEST-007 / app §A18.3). */
export const PHASE01_REQUIRED: readonly string[] = Object.keys(TRACE_MAP);
