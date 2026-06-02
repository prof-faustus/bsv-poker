/**
 * Game registry (app §A21.2): variant -> module factory. Adding a game is adding a registry
 * entry + a module, not editing screens (REQ-APP-213). game-holdem is registered here; the
 * other variant modules register as they land (Phase 3).
 */

import type { Card, GameState, Variant } from '@bsv-poker/protocol-types';
import type { GameModule } from '@bsv-poker/engine';
import { createHoldem } from '@bsv-poker/game-holdem';

export interface ModuleConfig {
  readonly deck: readonly Card[];
}
export type ModuleFactory = (config: ModuleConfig) => GameModule<GameState> & {
  stateHash: (s: GameState) => string;
};

const registry = new Map<Variant, ModuleFactory>();

export function registerGame(variant: Variant, factory: ModuleFactory): void {
  registry.set(variant, factory);
}

export function getGame(variant: Variant): ModuleFactory {
  const f = registry.get(variant);
  if (!f) throw new Error(`no game module registered for variant: ${variant}`);
  return f;
}

export function registeredVariants(): Variant[] {
  return [...registry.keys()];
}

// Register the built-in module(s). The module operates on its own concrete state type
// (HoldemState extends GameState); the registry hands it back opaquely as GameState — state
// only ever flows from this same module's init/apply, so the cast is runtime-safe.
registerGame('holdem', (cfg) => createHoldem({ deck: cfg.deck }) as unknown as ReturnType<ModuleFactory>);
