/** Sizes a canvas to its CSS box at device resolution and calls back whenever that changes. */
export function fitCanvas(canvas: HTMLCanvasElement, onResize: (width: number, height: number) => void): void {
  const apply = () => {
    const ratio = window.devicePixelRatio || 1;
    const { width, height } = canvas.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(width * ratio));
    canvas.height = Math.max(1, Math.round(height * ratio));
    canvas.getContext("2d")!.setTransform(ratio, 0, 0, ratio, 0, 0);
    onResize(width, height);
  };
  new ResizeObserver(apply).observe(canvas);
  apply();
}

export type Palette = {
  bg: string;
  surface: string;
  fg: string;
  muted: string;
  rule: string;
  excitatory: string;
  inhibitory: string;
  sensory: string;
  output: string;
  select: string;
};

/** Canvas can't read CSS variables by itself, so the views take colours from here. */
export function readPalette(): Palette {
  const style = getComputedStyle(document.documentElement);
  const token = (name: string) => style.getPropertyValue(name).trim();
  return {
    bg: token("--bg"),
    surface: token("--surface"),
    fg: token("--fg"),
    muted: token("--muted"),
    rule: token("--rule"),
    excitatory: token("--excitatory"),
    inhibitory: token("--inhibitory"),
    sensory: token("--sensory"),
    output: token("--output"),
    select: token("--select"),
  };
}

export function neuronColour(palette: Palette, model: { sensory: boolean[]; inhibitory: boolean[]; labels: (string | null)[] }, neuron: number): string {
  if (model.labels[neuron]) return palette.output;
  if (model.sensory[neuron]) return palette.sensory;
  return model.inhibitory[neuron] ? palette.inhibitory : palette.excitatory;
}
