import { fitCanvas, neuronColour, type Palette } from "./canvas";
import type { Subject } from "./subject";

const WINDOW_MS = 200;

/** A mark per spike, scrolling with simulated time. What a row stands for is up to the subject. */
export class RasterView {
  private readonly context: CanvasRenderingContext2D;
  private subject: Subject | null = null;
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

  setSubject(subject: Subject): void {
    this.subject = subject;
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
    const subject = this.subject;
    c.clearRect(0, 0, this.width, this.height);
    if (!subject || this.width === 0) return;

    const windowSteps = WINDOW_MS / subject.dtMs;
    const from = step - windowSteps;
    const firstKept = this.spikes.findIndex((s) => s.step >= from);
    if (firstKept > 0) this.spikes.splice(0, firstKept);
    else if (firstKept < 0) this.spikes = [];

    const left = 4;
    const plotWidth = this.width - left - 4;
    const top = 4;
    const plotHeight = this.height - top - 18;
    const rowHeight = plotHeight / subject.rasterRows;
    const mark = Math.max(1.5, Math.min(3, rowHeight));

    c.strokeStyle = this.palette.rule;
    c.lineWidth = 1;
    for (const row of subject.rasterDividers) {
      const y = Math.round(top + row * rowHeight) + 0.5;
      c.beginPath();
      c.moveTo(left, y);
      c.lineTo(left + plotWidth, y);
      c.stroke();
    }

    if (this.selected !== null) {
      c.globalAlpha = 0.18;
      c.fillStyle = this.palette.select;
      c.fillRect(left, top + subject.rasterRow(this.selected) * rowHeight - 2, plotWidth, Math.max(rowHeight, 1) + 4);
      c.globalAlpha = 1;
    }

    for (const { step: at, neuron } of this.spikes) {
      c.fillStyle = neuronColour(this.palette, subject, neuron);
      const x = left + ((at - from) / windowSteps) * plotWidth;
      c.fillRect(x - mark / 2, top + subject.rasterRow(neuron) * rowHeight, mark, Math.max(mark, rowHeight));
    }

    c.fillStyle = this.palette.muted;
    c.font = "11px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";
    c.textAlign = "left";
    c.fillText(`${Math.max(0, from * subject.dtMs).toFixed(0)} ms`, left, this.height - 4);
    c.textAlign = "right";
    c.fillText(`${(step * subject.dtMs).toFixed(1)} ms`, left + plotWidth, this.height - 4);
  }
}
