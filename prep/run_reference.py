"""Run the unmodified Shiu et al. Brian2 model on v783 and save per-neuron firing rates as ground truth."""

import argparse
import sys
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parent.parent
REF = ROOT / "data" / "ref" / "Drosophila_brain_model"
sys.path.insert(0, str(REF))

from model import default_params, run_exp  # noqa: E402

PRESETS = ROOT / "presets"


def read_preset(name: str) -> list[int]:
    lines = (PRESETS / f"{name}.txt").read_text().splitlines()
    return [int(l) for l in lines if l.strip() and not l.startswith("#")]


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("experiment", choices=[f.stem for f in PRESETS.glob("*.txt")])
    p.add_argument("--n-proc", type=int, default=8)
    p.add_argument("--out", type=Path, default=ROOT / "data" / "reference")
    a = p.parse_args()

    a.out.mkdir(parents=True, exist_ok=True)
    comp_path = REF / "Completeness_783.csv"
    known = set(pd.read_csv(comp_path, index_col=0).index)
    ids = read_preset(a.experiment)
    missing = [i for i in ids if i not in known]
    if missing:
        print(f"skipping {len(missing)} IDs absent from v783: {missing}")
    ids = [i for i in ids if i in known]

    run_exp(
        exp_name=a.experiment,
        neu_exc=ids,
        path_res=a.out,
        path_comp=comp_path,
        path_con=REF / "Connectivity_783.parquet",
        params=default_params,
        n_proc=a.n_proc,
    )

    spikes = pd.read_parquet(a.out / f"{a.experiment}.parquet")
    n_run = default_params["n_run"]
    t_run_s = float(default_params["t_run"])
    counts = spikes.groupby(["flywire_id", "trial"]).size().unstack(fill_value=0)
    counts = counts.reindex(columns=range(n_run), fill_value=0)
    rates = pd.DataFrame({
        "rate_hz": counts.mean(axis=1) / t_run_s,
        "std_hz": counts.std(axis=1, ddof=0) / t_run_s,
    }).sort_values("rate_hz", ascending=False)
    rates.index.name = "flywire_id"
    rates.to_csv(a.out / f"{a.experiment}_rates.csv")
    print(f"{len(rates)} active neurons; top 10:\n{rates.head(10)}")


if __name__ == "__main__":
    np.seterr(all="ignore")
    main()
