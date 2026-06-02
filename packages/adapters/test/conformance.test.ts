import { test } from 'node:test';
import {
  makeFakeCT,
  makeFakeBS,
  makeFakeVA,
  makeFakeOB,
} from '../src/index.ts';
import {
  runCTConformance,
  runBSConformance,
  runVAConformance,
  runOBConformance,
} from '../src/conformance.ts';

// REQ-DEP-003: the SAME suite runs against the fake (here) and the real adapter
// (crypto-mentalpoker's RealCT runs it in its own test) — both MUST pass.
test('CT fake passes the CT conformance suite', async () => {
  await runCTConformance(makeFakeCT());
});
test('BS fake passes the BS conformance suite', async () => {
  await runBSConformance(makeFakeBS());
});
test('VA fake passes the VA conformance suite', async () => {
  await runVAConformance(makeFakeVA());
});
test('OB fake passes the OB conformance suite', async () => {
  await runOBConformance(makeFakeOB());
});
