"""Build brain.meta.json: a position, home region, class and cell type for every neuron in brain.bin.

Sources:
    neuron_annotations.tsv                      flyconnectome/flywire_annotations, Supplemental file 1
    per_neuron_neuropil_count_{pre,post}_783    FlyWire v783 on Zenodo (record 10676866)

A neuron's home region is the neuropil holding most of its synapses, inputs and outputs together.
Everything is stored as parallel arrays in brain.bin order, with strings interned into lookup tables.
"""

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parent.parent


def intern(values: pd.Series) -> tuple[list[str], list[int]]:
    codes, names = pd.factorize(values.fillna("").astype(str), sort=True)
    return list(names), codes.tolist()


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--flywire", type=Path, default=ROOT / "data" / "flywire")
    p.add_argument("--comp", type=Path, default=ROOT / "data" / "ref" / "Drosophila_brain_model" / "Completeness_783.csv")
    p.add_argument("--out", type=Path, default=ROOT / "data" / "brain.meta.json")
    a = p.parse_args()

    ids = pd.read_csv(a.comp, index_col=0).index
    notes = pd.read_csv(a.flywire / "neuron_annotations.tsv", sep="\t", low_memory=False)
    notes = notes.drop_duplicates("root_id").set_index("root_id").reindex(ids)

    pre = pd.read_feather(a.flywire / "per_neuron_neuropil_count_pre_783.feather")
    post = pd.read_feather(a.flywire / "per_neuron_neuropil_count_post_783.feather")
    pre.columns = post.columns = ["root_id", "neuropil", "count"]
    counts = pd.concat([pre, post]).groupby(["root_id", "neuropil"], observed=True)["count"].sum().reset_index()
    home = counts.sort_values("count").drop_duplicates("root_id", keep="last").set_index("root_id")["neuropil"]
    home = home.reindex(ids)

    regions, region = intern(home.astype(object))
    classes, cls = intern(notes["super_class"])
    types, cell_type = intern(notes["cell_type"].fillna(notes["hemibrain_type"]))

    # Voxels are 4 x 4 x 40 nm; convert to micrometres so the three axes share a scale.
    um = notes[["pos_x", "pos_y", "pos_z"]].to_numpy(float) * np.array([4, 4, 40]) / 1000
    missing = np.isnan(um).any(axis=1)
    um[missing] = np.nanmedian(um, axis=0)

    meta = {
        "neurons": len(ids),
        "regions": regions,
        "classes": classes,
        "types": types,
        "region": region,
        "class": cls,
        "type": cell_type,
        "x": np.round(um[:, 0], 1).tolist(),
        "y": np.round(um[:, 1], 1).tolist(),
        "z": np.round(um[:, 2], 1).tolist(),
    }
    a.out.write_text(json.dumps(meta, separators=(",", ":")))

    sizes = pd.Series(region).map(lambda r: regions[r] or "(none)").value_counts()
    print(f"{len(ids)} neurons, {len(regions)} regions, {len(classes)} classes, {len(types)} cell types")
    print(f"no position: {int(missing.sum())}, no region: {int((home.isna()).sum())}")
    print("largest regions:", ", ".join(f"{k} {v}" for k, v in sizes.head(6).items()))
    print(f"wrote {a.out} ({a.out.stat().st_size / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
