import type { ClientMessage, ServerMessage } from "./protocol";

export type ConnectionState = "connecting" | "open" | "closed";

/** WebSocket to the simulator that reconnects by itself. */
export class Connection {
  private socket: WebSocket | null = null;
  private retryMs = 500;

  constructor(
    private readonly onMessage: (message: ServerMessage) => void,
    private readonly onState: (state: ConnectionState) => void,
  ) {
    this.open();
  }

  send(message: ClientMessage): void {
    if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify(message));
  }

  private open(): void {
    this.onState("connecting");
    const scheme = location.protocol === "https:" ? "wss" : "ws";
    const socket = new WebSocket(`${scheme}://${location.host}/ws`);
    this.socket = socket;

    socket.onopen = () => {
      this.retryMs = 500;
      this.onState("open");
    };
    socket.onmessage = (event) => this.onMessage(JSON.parse(event.data as string) as ServerMessage);
    socket.onclose = () => {
      this.onState("closed");
      setTimeout(() => this.open(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, 5000);
    };
  }
}
