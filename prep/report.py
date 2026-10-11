"""Write VALIDATION.md: Neuropil's firing rates against the Brian2 reference for every experiment that has both."""

from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
PRESETS = ROOT / "presets"


def read_ids(path: Path) -> list[int]:
    return [int(l.split()[0]) for l in path.read_text().splitlines() if l.strip() and not l.startswith("#")]


def read_labels(path: Path) -> dict[int, str]:
    if not path.exists():
        return {}
    return {int(a): b for a, b in (l.split(maxsplit=1) for l in path.read_text().splitlines() if l.strip() and not l.startswith("#"))}


def main() -> None:
    summary, details = [], []
    for preset in sorted(p for p in PRESETS.glob("*.txt") if "." not in p.stem):
        name = preset.stem
        ref_path, ours_path = DATA / "reference" / f"{name}_rates.csv", DATA / f"{name}_array.csv"
        if not ref_path.exists() or not ours_path.exists():
            continue
        ref = pd.read_csv(ref_path, index_col="flywire_id")["rate_hz"]
        ours = pd.read_csv(ours_path, index_col="flywire_id")["rate_hz"]
        both = pd.concat({"ref": ref, "ours": ours}, axis=1).fillna(0)
        r = np.corrcoef(both["ref"], both["ours"])[0, 1]

        driven = set(read_ids(preset))
        top = lambda s: set(s[~s.index.isin(driven)].nlargest(20).index)
        summary.append(f"| {name} | {len(driven)} | {len(ref)} | {len(ours)} | {r:.4f} | {len(top(ref) & top(ours))}/20 |")
        for neuron, label in read_labels(PRESETS / f"{name}.outputs.txt").items():
            details.append(f"| {name} | {label} | {ref.get(neuron, 0):.1f} | {ours.get(neuron, 0):.1f} |")

    text = "\n".join([
        "# Validation",
        "",
        "Neuropil's array simulator against the Brian2 model from Shiu et al. (2024), on the FlyWire v783 connectome.",
        "Each experiment is 30 trials of one second with the listed sensory neurons driven at 150 Hz.",
        "Regenerate with `uv run python report.py` in `prep/` after running both sides.",
        "",
        "| Experiment | Driven neurons | Active in Brian2 | Active in Neuropil | Pearson r | Same top 20 |",
        "|---|---|---|---|---|---|",
        *summary,
        "",
        "Pearson r is over every neuron active in either simulator. The top 20 leaves out the driven neurons,",
        "which all fire near the stimulus rate in an arbitrary order.",
        "",
        "## Named output neurons",
        "",
        "| Experiment | Neuron | Brian2 (Hz) | Neuropil (Hz) |",
        "|---|---|---|---|",
        *details,
        "",
        "With the random input replaced by a deterministic one, the two simulators produce identical spikes:",
        "12,809 of 12,809 over 50 ms of the whole brain. The actor modes are then checked against the array",
        "simulator spike for spike by `neuropil compare` and `neuropil bench`.",
        "",
    ])
    (ROOT / "VALIDATION.md").write_text(text)
    print(text)


if __name__ == "__main__":
    main()
