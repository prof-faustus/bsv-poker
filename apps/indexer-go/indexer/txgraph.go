// Canonical, validated transaction graph (audit finding #25), maintained BY the indexer in Go.
//
// REQ-NET-001/004 (P3): the indexer is a projection, never the ultimate source of truth (the node is)
// — but the projection it serves for the on-chain settlement records is now the VALIDATED canonical
// transaction graph, not opaque bytes. It reconstructs the DAG from raw transactions and enforces the
// consensus structural invariants every honest node enforces, so any client rebuilds the identical
// graph (P2) and it matches what the node accepted:
//
//   - PARENT EXISTENCE: every input spends an output that exists in the graph (or a registered root);
//   - NO DOUBLE-SPEND: each outpoint is spent at most once;
//   - VALUE CONSERVATION: a tx's outputs never exceed its inputs (the difference is the fee).
//
// Stdlib only (crypto/sha256, encoding/binary) — no external dependency, no second consensus engine;
// script execution / signatures remain the node's job (full validation), this is the structural graph.
package indexer

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"fmt"
)

// TxGraph is the canonical UTXO DAG built from raw transactions.
type TxGraph struct {
	utxo  map[string]uint64 // "txid:vout" -> satoshis (unspent)
	spent map[string]string // "txid:vout" -> spending txid
	txs   map[string]struct{}
}

func NewTxGraph() *TxGraph {
	return &TxGraph{utxo: map[string]uint64{}, spent: map[string]string{}, txs: map[string]struct{}{}}
}

func opKey(txid string, vout uint32) string { return fmt.Sprintf("%s:%d", txid, vout) }

// AddRoot registers a pre-existing outpoint (e.g. a mined coinbase) the graph may spend from.
func (g *TxGraph) AddRoot(txid string, vout uint32, satoshis uint64) {
	g.utxo[opKey(txid, vout)] = satoshis
}

// Add validates and folds a raw transaction (hex) into the graph: parent existence, no double-spend,
// value conservation. Returns the txid on success, or an error (the graph is unchanged on error).
func (g *TxGraph) Add(rawHex string) (string, error) {
	raw, err := hex.DecodeString(rawHex)
	if err != nil {
		return "", fmt.Errorf("not valid hex: %w", err)
	}
	inputs, outputs, err := parseTx(raw)
	if err != nil {
		return "", fmt.Errorf("parse: %w", err)
	}
	txid := txidOf(raw)
	if _, dup := g.txs[txid]; dup {
		return "", fmt.Errorf("duplicate txid %s", txid)
	}
	var inSum uint64
	spends := make([]string, 0, len(inputs))
	for _, in := range inputs {
		key := opKey(in.prevTxid, in.vout)
		if by, ds := g.spent[key]; ds {
			return "", fmt.Errorf("double-spend of %s (already spent by %s)", key, by)
		}
		val, ok := g.utxo[key]
		if !ok {
			return "", fmt.Errorf("input %s has no producing output in the graph (missing parent)", key)
		}
		inSum += val
		spends = append(spends, key)
	}
	var outSum uint64
	for _, o := range outputs {
		outSum += o
	}
	if outSum > inSum {
		return "", fmt.Errorf("value creation: outputs %d > inputs %d", outSum, inSum)
	}
	// Commit (all checks passed).
	g.txs[txid] = struct{}{}
	for _, key := range spends {
		g.spent[key] = txid
		delete(g.utxo, key)
	}
	for i, o := range outputs {
		g.utxo[opKey(txid, uint32(i))] = o
	}
	return txid, nil
}

// IsUnspent reports whether an outpoint is unspent in the canonical graph.
func (g *TxGraph) IsUnspent(txid string, vout uint32) bool {
	_, ok := g.utxo[opKey(txid, vout)]
	return ok
}

// UTXOValue is the total value of the canonical unspent set.
func (g *TxGraph) UTXOValue() uint64 {
	var s uint64
	for _, v := range g.utxo {
		s += v
	}
	return s
}

// Size is the number of transactions in the graph (roots excluded).
func (g *TxGraph) Size() int { return len(g.txs) }

// txidOf computes the display txid (big-endian hex) of a raw tx = reverse(double-SHA256(raw)).
func txidOf(raw []byte) string {
	h1 := sha256.Sum256(raw)
	h2 := sha256.Sum256(h1[:])
	rev := make([]byte, 32)
	for i := 0; i < 32; i++ {
		rev[i] = h2[31-i]
	}
	return hex.EncodeToString(rev)
}

type parsedInput struct {
	prevTxid string // display (big-endian) hex
	vout     uint32
}

// parseTx parses a BSV wire transaction, returning its inputs (prev outpoints) and output values.
// Bounded and defensive: every length is checked against the remaining bytes (no panic on hostile input).
func parseTx(b []byte) (inputs []parsedInput, outputs []uint64, err error) {
	p := 0
	need := func(n int) error {
		if p+n > len(b) {
			return fmt.Errorf("truncated at offset %d (need %d, have %d)", p, n, len(b)-p)
		}
		return nil
	}
	readVarInt := func() (uint64, error) {
		if err := need(1); err != nil {
			return 0, err
		}
		first := b[p]
		p++
		switch {
		case first < 0xfd:
			return uint64(first), nil
		case first == 0xfd:
			if err := need(2); err != nil {
				return 0, err
			}
			v := uint64(binary.LittleEndian.Uint16(b[p : p+2]))
			p += 2
			return v, nil
		case first == 0xfe:
			if err := need(4); err != nil {
				return 0, err
			}
			v := uint64(binary.LittleEndian.Uint32(b[p : p+4]))
			p += 4
			return v, nil
		default:
			if err := need(8); err != nil {
				return 0, err
			}
			v := binary.LittleEndian.Uint64(b[p : p+8])
			p += 8
			return v, nil
		}
	}

	if err := need(4); err != nil { // version
		return nil, nil, err
	}
	p += 4

	vin, err := readVarInt()
	if err != nil {
		return nil, nil, err
	}
	if vin > uint64(len(b)) {
		return nil, nil, fmt.Errorf("input count %d exceeds size", vin)
	}
	for i := uint64(0); i < vin; i++ {
		if err := need(32); err != nil {
			return nil, nil, err
		}
		// prevTxid is little-endian on the wire; expose big-endian display hex (reverse).
		rev := make([]byte, 32)
		for k := 0; k < 32; k++ {
			rev[k] = b[p+31-k]
		}
		p += 32
		if err := need(4); err != nil {
			return nil, nil, err
		}
		vout := binary.LittleEndian.Uint32(b[p : p+4])
		p += 4
		sl, err := readVarInt()
		if err != nil {
			return nil, nil, err
		}
		if err := need(int(sl)); err != nil {
			return nil, nil, err
		}
		p += int(sl) // scriptSig (not interpreted here)
		if err := need(4); err != nil {
			return nil, nil, err
		}
		p += 4 // sequence
		inputs = append(inputs, parsedInput{prevTxid: hex.EncodeToString(rev), vout: vout})
	}

	vout, err := readVarInt()
	if err != nil {
		return nil, nil, err
	}
	if vout > uint64(len(b)) {
		return nil, nil, fmt.Errorf("output count %d exceeds size", vout)
	}
	for i := uint64(0); i < vout; i++ {
		if err := need(8); err != nil {
			return nil, nil, err
		}
		val := binary.LittleEndian.Uint64(b[p : p+8])
		p += 8
		sl, err := readVarInt()
		if err != nil {
			return nil, nil, err
		}
		if err := need(int(sl)); err != nil {
			return nil, nil, err
		}
		p += int(sl) // locking script (not interpreted here)
		outputs = append(outputs, val)
	}

	if err := need(4); err != nil { // nLockTime
		return nil, nil, err
	}
	p += 4
	return inputs, outputs, nil
}
