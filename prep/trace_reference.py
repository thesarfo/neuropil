"""Run one Brian2 trial of the reference model and write every spike as step,flywire_id.

With --rate at or above 1/dt the Poisson input fires every step it can, which makes the run
deterministic and lets Neuropil be checked spike for spike.
"""

import argparse
import sys
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parent.parent
REF = ROOT / "data" / "ref" / "Drosophila_brain_model"
sys.path.insert(0, str(REF))

from brian2 import Hz, ms, Network, prefs  # noqa: E402
from model import create_model, default_params, poi  # noqa: E402
from run_reference import read_preset  # noqa: E402


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("experiment")
    p.add_argument("--ms", type=float, default=100)
    p.add_argument("--rate", type=float, default=150)
    p.add_argument("--out", type=Path, required=True)
    a = p.parse_args()

    prefs.codegen.target = "numpy"
    comp_path = REF / "Completeness_783.csv"
    flyids = pd.read_csv(comp_path, index_col=0).index.to_numpy()
    index = {f: i for i, f in enumerate(flyids)}
    exc = [index[f] for f in read_preset(a.experiment) if f in index]

    params = dict(default_params, r_poi=a.rate * Hz)
    neu, syn, spk = create_model(comp_path, REF / "Connectivity_783.parquet", params)
    pois, neu = poi(neu, exc, [], params)
    Network(neu, syn, spk, *pois).run(a.ms * ms)

    steps = np.round(np.asarray(spk.t / ms) * 10).astype(int)
    pd.DataFrame({"step": steps, "flywire_id": flyids[np.asarray(spk.i)]}).to_csv(a.out, index=False)
    print(f"wrote {len(steps)} spikes to {a.out}")


if __name__ == "__main__":
    main()
