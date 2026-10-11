// End-to-end check of the WebSocket protocol. The same code runs pasted into a browser console.
//   node --experimental-websocket scripts/smoke.mjs
const ws = new WebSocket("ws://127.0.0.1:5080/ws");
const send = (m) => ws.send(JSON.stringify(m));
const seen = { frames: 0, spikes: 0, firstFrameAt: 0, lastStep: 0, neurons: new Set(), errors: [] };
let phase = "connecting";
let pausedStep = -1;

ws.onmessage = ({ data }) => {
  const m = JSON.parse(data);
  if (m.type === "hello") {
    console.log("circuits:", m.circuits.map((c) => `${c.name} (${c.neurons} neurons)`).join(", "));
    send({ type: "load", circuit: m.circuits[0].name });
  } else if (m.type === "circuit") {
    console.log(`loaded ${m.name}: ${m.neurons.length} neurons, ${m.edges.length} edges, ${m.neurons.filter((n) => n.sensory).length} sensory`);
    send({ type: "bogus" });
    send({ type: "stimulate", rateHz: 150 });
    send({ type: "speed", value: 0.1 });
    send({ type: "play" });
    phase = "playing";
    setTimeout(() => { send({ type: "pause" }); phase = "pausing"; }, 2000);
  } else if (m.type === "error") {
    seen.errors.push(m.message);
  } else if (m.type === "frame") {
    if (phase === "playing") {
      seen.frames++;
      seen.firstFrameAt ||= Date.now();
      seen.spikes += m.spikes.length;
      for (const [, n] of m.spikes) seen.neurons.add(n);
      seen.lastStep = m.step;
      seen.lastAt = Date.now();
    } else if (phase === "pausing" && !m.running) {
      pausedStep = m.step;
      phase = "stepping";
      send({ type: "step" });
    } else if (phase === "stepping" && m.step > pausedStep) {
      const secs = (seen.lastAt - seen.firstFrameAt) / 1000;
      console.log(`played ${secs.toFixed(1)} s: ${seen.frames} frames (${((seen.frames - 1) / secs).toFixed(1)}/s), ${seen.spikes} spikes from ${seen.neurons.size} neurons, reached ${(seen.lastStep / 10).toFixed(1)} ms of simulated time`);
      console.log(`paused at step ${pausedStep}; one step moved it to ${m.step}`);
      console.log(`errors received: ${JSON.stringify(seen.errors)}`);
      phase = "resetting";
      send({ type: "reset" });
    } else if (phase === "resetting" && m.step === 0) {
      console.log("reset back to step 0");
      const ok = seen.spikes > 0 && (seen.frames - 1) / ((seen.lastAt - seen.firstFrameAt) / 1000) <= 31 && m.step === 0 && seen.errors.length === 1;
      console.log(ok ? "OK" : "FAILED");
      ws.close();
      process?.exit?.(ok ? 0 : 1);
    }
  }
};
ws.onerror = (e) => { console.error("socket error", e.message ?? e); process?.exit?.(1); };
