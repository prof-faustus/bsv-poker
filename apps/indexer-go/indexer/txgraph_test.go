// Canonical transaction-graph tests (audit #25): a funding→settlement DAG validates and tracks the
// UTXO set; double-spend, value-creation and missing-parent are rejected. Builds real wire txs that
// the parser round-trips (and whose txid is reverse(double-SHA256), matching the production code).
package indexer

import (
	"encoding/binary"
	"encoding/hex"
	"testing"
)

type tin struct {
	prevTxid string
	vout     uint32
}
type tout struct{ value uint64 }

func putVarInt(b *[]byte, n uint64) {
	switch {
	case n < 0xfd:
		*b = append(*b, byte(n))
	case n <= 0xffff:
		var x [2]byte
		binary.LittleEndian.PutUint16(x[:], uint16(n))
		*b = append(*b, 0xfd)
		*b = append(*b, x[:]...)
	default:
		var x [4]byte
		binary.LittleEndian.PutUint32(x[:], uint32(n))
		*b = append(*b, 0xfe)
		*b = append(*b, x[:]...)
	}
}

func buildTx(ins []tin, outs []tout) (rawHex, txid string) {
	var b []byte
	b = append(b, 0, 0, 0, 0) // version
	putVarInt(&b, uint64(len(ins)))
	for _, in := range ins {
		d, _ := hex.DecodeString(in.prevTxid)
		rev := make([]byte, 32)
		for i := 0; i < 32; i++ {
			rev[i] = d[31-i] // display(BE) -> wire(LE)
		}
		b = append(b, rev...)
		v := make([]byte, 4)
		binary.LittleEndian.PutUint32(v, in.vout)
		b = append(b, v...)
		putVarInt(&b, 0)          // empty scriptSig
		b = append(b, 0, 0, 0, 0) // sequence
	}
	putVarInt(&b, uint64(len(outs)))
	for _, o := range outs {
		val := make([]byte, 8)
		binary.LittleEndian.PutUint64(val, o.value)
		b = append(b, val...)
		putVarInt(&b, 1)
		b = append(b, 0x51) // 1-byte locking script (content irrelevant to the graph)
	}
	b = append(b, 0, 0, 0, 0) // nLockTime
	return hex.EncodeToString(b), txidOf(b)
}

const rootTxid = "abababababababababababababababababababababababababababababababab"

func seeded(t *testing.T, value uint64) *TxGraph {
	g := NewTxGraph()
	g.AddRoot(rootTxid, 0, value)
	return g
}

func TestTxGraphFundingSettlementValidates(t *testing.T) {
	g := seeded(t, 1000)
	fundRaw, fundTxid := buildTx([]tin{{rootTxid, 0}}, []tout{{900}})
	if _, err := g.Add(fundRaw); err != nil {
		t.Fatalf("funding must validate: %v", err)
	}
	setRaw, setTxid := buildTx([]tin{{fundTxid, 0}}, []tout{{500}, {380}})
	if _, err := g.Add(setRaw); err != nil {
		t.Fatalf("settlement must validate: %v", err)
	}
	if g.IsUnspent(fundTxid, 0) {
		t.Fatal("funding output must be spent")
	}
	if !g.IsUnspent(setTxid, 0) || !g.IsUnspent(setTxid, 1) {
		t.Fatal("settlement outputs must be unspent")
	}
	if g.UTXOValue() != 880 {
		t.Fatalf("UTXO value = settlement payouts; got %d", g.UTXOValue())
	}
	if g.Size() != 2 {
		t.Fatalf("graph size = 2; got %d", g.Size())
	}
}

func TestTxGraphRejectsDoubleSpend(t *testing.T) {
	g := seeded(t, 1000)
	fundRaw, fundTxid := buildTx([]tin{{rootTxid, 0}}, []tout{{900}})
	if _, err := g.Add(fundRaw); err != nil {
		t.Fatal(err)
	}
	s1, _ := buildTx([]tin{{fundTxid, 0}}, []tout{{800}})
	s2, _ := buildTx([]tin{{fundTxid, 0}}, []tout{{700}})
	if _, err := g.Add(s1); err != nil {
		t.Fatal(err)
	}
	if _, err := g.Add(s2); err == nil {
		t.Fatal("a double-spend of the funding output must be rejected")
	}
}

func TestTxGraphRejectsValueCreation(t *testing.T) {
	g := seeded(t, 1000)
	inflate, _ := buildTx([]tin{{rootTxid, 0}}, []tout{{2000}})
	if _, err := g.Add(inflate); err == nil {
		t.Fatal("outputs exceeding inputs (value creation) must be rejected")
	}
}

func TestTxGraphRejectsMissingParent(t *testing.T) {
	g := seeded(t, 1000)
	orphan, _ := buildTx([]tin{{"cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", 7}}, []tout{{10}})
	if _, err := g.Add(orphan); err == nil {
		t.Fatal("spending a missing parent output must be rejected")
	}
}

// The Indexer ITSELF maintains the canonical graph (audit #25): funding→settlement tracked, UTXO set
// correct, and a double-spend rejected — through the indexer's own methods.
func TestIndexerMaintainsCanonicalGraph(t *testing.T) {
	ix := New()
	ix.AddTxRoot(rootTxid, 0, 1000)
	fundRaw, fundTxid := buildTx([]tin{{rootTxid, 0}}, []tout{{900}})
	if _, err := ix.IngestTx(fundRaw); err != nil {
		t.Fatalf("funding must be ingested: %v", err)
	}
	setRaw, _ := buildTx([]tin{{fundTxid, 0}}, []tout{{880}})
	if _, err := ix.IngestTx(setRaw); err != nil {
		t.Fatalf("settlement must be ingested: %v", err)
	}
	if ix.TxUnspent(fundTxid, 0) {
		t.Fatal("funding output must be spent in the indexer's canonical graph")
	}
	if ix.UTXOValue() != 880 {
		t.Fatalf("canonical UTXO value = 880; got %d", ix.UTXOValue())
	}
	ds, _ := buildTx([]tin{{fundTxid, 0}}, []tout{{100}})
	if _, err := ix.IngestTx(ds); err == nil {
		t.Fatal("the indexer must reject a double-spend in its canonical graph")
	}
}
