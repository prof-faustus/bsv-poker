/**
 * Conformance-against-real E2E (REQ-DEP-003, RT-02 F2). The SAME contract conformance suite that
 * the fakes pass (packages/adapters/test/conformance.test.ts) is run here against the REAL
 * verifiable-accounting adapter — proving the real implementation satisfies the same invariants
 * (INV-VA-2 boundary + Merkle inclusion/tamper-rejection), not just a conformant fake.
 */

import { runVAConformance } from '@bsv-poker/adapters/conformance';
import { realVAContract } from '@bsv-poker/adapters/real-va';

async function main(): Promise<void> {
  await runVAConformance(realVAContract());
  console.log('[conformance-real] VA: the REAL @vaa/merkle adapter passes the same conformance suite as the fake.');
  console.log('\n[conformance-real] PASS — REQ-DEP-003 satisfied for VA against the real implementation.');
}

main().then(() => process.exit(0), (e) => { console.error('[conformance-real] FAIL:', (e as Error).message); process.exit(1); });
