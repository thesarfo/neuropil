"""Compare Neuropil firing rates with the Brian2 reference: correlation, top-N overlap, and named neurons."""

import argparse
from pathlib import Path

import numpy as np
import pandas as pd

MN9 = 720575940660219265  # proboscis motor neuron the reference reports for sugar


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("reference", type=Path)
    p.add_argument("ours", type=Path)
    p.add_argument("--top", type=int, default=20)
    a = p.parse_args()

    ref = pd.read_csv(a.reference, index_col="flywire_id")["rate_hz"]
    ours = pd.read_csv(a.ours, index_col="flywire_id")["rate_hz"]
    both = pd.concat({"reference": ref, "ours": ours}, axis=1).fillna(0)

    r = np.corrcoef(both["reference"], both["ours"])[0, 1]
    top_ref = set(ref.nlargest(a.top).index)
    top_ours = set(ours.nlargest(a.top).index)

    print(f"active neurons    reference={len(ref)} ours={len(ours)} union={len(both)}")
    print(f"pearson r         {r:.4f}  (over the union of active neurons)")
    print(f"top-{a.top} overlap    {len(top_ref & top_ours)}/{a.top}")
    print(f"MN9 rate (Hz)     reference={ref.get(MN9, 0):.2f} ours={ours.get(MN9, 0):.2f}")

    both["diff"] = both["ours"] - both["reference"]
    print("\nlargest disagreements:")
    print(both.reindex(both["diff"].abs().sort_values(ascending=False).index).head(10).to_string())


if __name__ == "__main__":
    main()
