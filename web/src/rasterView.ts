import { fitCanvas, neuronColour, type Palette } from "./canvas";
import type { CircuitModel } from "./model";

const WINDOW_MS = 200;

/** One row per neuron, a mark per spike, scrolling with simulated time. Rows run from sensors (top) to the deepest layer. */
export class RasterView {
  private readonly context: CanvasRenderingContext2D;
  private model: CircuitModel | null = null;
  private row = new Int32Array(0);
  private spikes: { step: number; neuron: number }[] = [];
  private width = 0;
  private height = 0;
  private selected: number | null = null;

  constructor(canvas: HTMLCanvasElement, private palette: Palette) {
    this.context = canvas.getContext("2d")!;
    fitCanvas(canvas, (width, height) => {
      this.width = width;
      this.height = height;
    });
  }

  setModel(model: CircuitModel): void {
    this.model = model;
    this.row = new Int32Array(model.count);
    model.layers.flat().forEach((neuron, position) => (this.row[neuron] = position));
    this.spikes = [];
  }

  setPalette(palette: Palette): void {
    this.palette = palette;
  }

  setSelected(neuron: number | null): void {
    this.selected = neuron;
  }

  add(step: number, neuron: number): void {
    this.spikes.push({ step, neuron });
  }

  clear(): void {
    this.spikes = [];
  }

  draw(step: number): void {
    const c = this.context;
    const model = this.model;
    c.clearRect(0, 0, this.width, this.height);
    if (!model || this.width === 0) return;

    const windowSteps = WINDOW_MS / model.dtMs;
    const from = step - windowSteps;
    const firstKept = this.spikes.findIndex((s) => s.step >= from);
    if (firstKept > 0) this.spikes.splice(0, firstKept);
    else if (firstKept < 0) this.spikes = [];

    const left = 4;
    const plotWidth = this.width - left - 4;
    const top = 4;
    const plotHeight = this.height - top - 18;
    const rowHeight = plotHeight / model.count;
    const mark = Math.max(1.5, Math.min(3, rowHeight));

    let rowStart = 0;
    c.strokeStyle = this.palette.rule;
    c.lineWidth = 1;
    for (const layer of model.layers.slice(0, -1)) {
      rowStart += layer.length;
      const y = Math.round(top + rowStart * rowHeight) + 0.5;
      c.beginPath();
      c.moveTo(left, y);
      c.lineTo(left + plotWidth, y);
      c.stroke();
    }

    if (this.selected !== null) {
      c.globalAlpha = 0.18;
      c.fillStyle = this.palette.select;
      c.fillRect(left, top + this.row[this.selected] * rowHeight - 2, plotWidth, Math.max(rowHeight, 1) + 4);
      c.globalAlpha = 1;
    }

    for (const { step: at, neuron } of this.spikes) {
      c.fillStyle = neuronColour(this.palette, model, neuron);
      const x = left + ((at - from) / windowSteps) * plotWidth;
      c.fillRect(x - mark / 2, top + this.row[neuron] * rowHeight, mark, Math.max(mark, rowHeight));
    }

    c.fillStyle = this.palette.muted;
    c.font = "11px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";
    c.textAlign = "left";
    c.fillText(`${Math.max(0, from * model.dtMs).toFixed(0)} ms`, left, this.height - 4);
    c.textAlign = "right";
    c.fillText(`${(step * model.dtMs).toFixed(1)} ms`, left + plotWidth, this.height - 4);
  }
}
