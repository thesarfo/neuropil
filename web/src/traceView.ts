import { CANVAS_MONO, fitCanvas, type Palette } from "./canvas";

const WINDOW_MS = 100;
const REST_MV = -52;
const THRESHOLD_MV = -45;

/** Voltage of the selected neuron over the last 100 ms of simulated time. */
export class TraceView {
  private readonly context: CanvasRenderingContext2D;
  private samples: number[] = [];
  private firstStep = 0;
  private width = 0;
  private height = 0;

  constructor(canvas: HTMLCanvasElement, private palette: Palette, private dtMs = 0.1) {
    this.context = canvas.getContext("2d")!;
    fitCanvas(canvas, (width, height) => {
      this.width = width;
      this.height = height;
    });
  }

  setPalette(palette: Palette): void {
    this.palette = palette;
  }

  clear(dtMs = this.dtMs): void {
    this.dtMs = dtMs;
    this.samples = [];
  }

  add(fromStep: number, millivolts: number[]): void {
    if (this.samples.length === 0 || fromStep !== this.firstStep + this.samples.length) {
      this.samples = [];
      this.firstStep = fromStep;
    }
    this.samples.push(...millivolts);
    const extra = this.samples.length - WINDOW_MS / this.dtMs;
    if (extra > 0) {
      this.samples.splice(0, extra);
      this.firstStep += extra;
    }
  }

  draw(): void {
    const c = this.context;
    c.clearRect(0, 0, this.width, this.height);
    if (this.width === 0) return;

    const low = REST_MV - 10;
    const high = THRESHOLD_MV + 3;
    const y = (mv: number) => this.height - 4 - ((Math.min(high, Math.max(low, mv)) - low) / (high - low)) * (this.height - 8);

    c.font = `10px ${CANVAS_MONO}`;
    c.lineWidth = 1;
    for (const [mv, name] of [[THRESHOLD_MV, "threshold"], [REST_MV, "rest"]] as const) {
      c.strokeStyle = this.palette.rule;
      c.setLineDash([4, 4]);
      c.beginPath();
      c.moveTo(0, y(mv));
      c.lineTo(this.width, y(mv));
      c.stroke();
      c.setLineDash([]);
      c.fillStyle = this.palette.muted;
      c.fillText(`${name} ${mv} mV`, 4, y(mv) - 3);
    }

    if (this.samples.length < 2) return;
    const windowSteps = WINDOW_MS / this.dtMs;
    c.strokeStyle = this.palette.fg;
    c.lineWidth = 1.4;
    c.beginPath();
    this.samples.forEach((mv, k) => {
      const x = ((windowSteps - this.samples.length + k) / windowSteps) * this.width;
      if (k === 0) c.moveTo(x, y(mv));
      else c.lineTo(x, y(mv));
    });
    c.stroke();
  }
}
