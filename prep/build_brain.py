"""Convert the Shiu et al. v783 connectome into brain.bin, a CSR file the C# side maps straight into arrays.

Layout (little-endian):
    magic        4 bytes  b"NPIL"
    version      int32    1
    neurons      int32    N
    edges        int64    E
    flywire_ids  int64[N] row i is the reference model's neuron index i
    row_ptr      int64[N+1] outgoing edges of neuron i are edges[row_ptr[i]:row_ptr[i+1]]
    targets      int32[E] postsynaptic neuron index
    weights      int16[E] signed synapse count ("Excitatory x Connectivity")

Weights stay as integers on purpose: summing integers is order-independent, so the
array and actor simulators can agree spike for spike. Multiply by w_syn at use.
"""

import argparse
import struct
from pathlib import Path

import numpy as np
import pandas as pd

MAGIC = b"NPIL"
VERSION = 1


def build(comp_path: Path, con_path: Path, out_path: Path) -> None:
    comp = pd.read_csv(comp_path, index_col=0)
    con = pd.read_parquet(
        con_path,
        columns=["Presynaptic_Index", "Postsynaptic_Index", "Excitatory x Connectivity"],
    )

    n = len(comp)
    pre = con["Presynaptic_Index"].to_numpy(np.int64)
    post = con["Postsynaptic_Index"].to_numpy(np.int64)
    w = con["Excitatory x Connectivity"].to_numpy(np.int64)

    assert pre.min() >= 0 and pre.max() < n and post.min() >= 0 and post.max() < n
    assert np.abs(w).max() <= np.iinfo(np.int16).max
    assert not np.any(w == 0), "zero-weight edges in source"

    order = np.lexsort((post, pre))
    pre, post, w = pre[order], post[order], w[order]
    dupes = np.count_nonzero((pre[1:] == pre[:-1]) & (post[1:] == post[:-1]))
    assert dupes == 0, f"{dupes} duplicate (pre, post) pairs"

    row_ptr = np.zeros(n + 1, dtype=np.int64)
    np.cumsum(np.bincount(pre, minlength=n), out=row_ptr[1:])

    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<iiq", VERSION, n, len(pre)))
        f.write(comp.index.to_numpy(np.int64).astype("<i8").tobytes())
        f.write(row_ptr.astype("<i8").tobytes())
        f.write(post.astype("<i4").tobytes())
        f.write(w.astype("<i2").tobytes())

    print(f"neurons={n} edges={len(pre)} synapses={np.abs(w).sum()} "
          f"excitatory_edges={np.count_nonzero(w > 0)} inhibitory_edges={np.count_nonzero(w < 0)}")
    print(f"wrote {out_path} ({out_path.stat().st_size / 1e6:.1f} MB)")


def main() -> None:
    ref = Path(__file__).resolve().parent.parent / "data" / "ref" / "Drosophila_brain_model"
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--comp", type=Path, default=ref / "Completeness_783.csv")
    p.add_argument("--con", type=Path, default=ref / "Connectivity_783.parquet")
    p.add_argument("--out", type=Path, default=ref.parent.parent / "brain.bin")
    a = p.parse_args()
    build(a.comp, a.con, a.out)


if __name__ == "__main__":
    main()
