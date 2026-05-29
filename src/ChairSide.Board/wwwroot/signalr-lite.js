(() => {
  const recordSeparator = String.fromCharCode(30);

  class HubConnectionBuilder {
    constructor() {
      this.url = "";
      this.reconnect = false;
    }

    withUrl(url) {
      this.url = url;
      return this;
    }

    withAutomaticReconnect() {
      this.reconnect = true;
      return this;
    }

    build() {
      return new HubConnection(this.url, this.reconnect);
    }
  }

  class HubConnection {
    constructor(url, reconnect) {
      this.url = url;
      this.reconnect = reconnect;
      this.handlers = new Map();
      this.socket = null;
      this.reconnectTimer = null;
      this.handshakeComplete = false;
      this.handshakeResolver = null;
      this.closeHandlers = [];
      this.reconnectingHandlers = [];
      this.reconnectedHandlers = [];
      this.state = "Disconnected";
    }

    on(target, handler) {
      this.handlers.set(target, handler);
    }

    onclose(handler) {
      this.closeHandlers.push(handler);
    }

    onreconnecting(handler) {
      this.reconnectingHandlers.push(handler);
    }

    onreconnected(handler) {
      this.reconnectedHandlers.push(handler);
    }

    send(target, ...args) {
      if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
        throw new Error("SignalR connection is not open.");
      }

      this.socket.send(JSON.stringify({
        type: 1,
        target,
        arguments: args
      }) + recordSeparator);
    }

    async start() {
      const negotiateUrl = `${this.url}/negotiate?negotiateVersion=1`;
      const response = await fetch(negotiateUrl, { method: "POST" });
      if (!response.ok) {
        throw new Error(`SignalR negotiate failed with HTTP ${response.status}.`);
      }

      const negotiate = await response.json();
      const token = negotiate.connectionToken || negotiate.connectionId;
      const wsUrl = toWebSocketUrl(`${this.url}?id=${encodeURIComponent(token)}`);

      await new Promise((resolve, reject) => {
        const socket = new WebSocket(wsUrl);
        this.socket = socket;
        this.state = "Connecting";
        this.handshakeComplete = false;
        this.handshakeResolver = resolve;

        socket.addEventListener("open", () => {
          socket.send(JSON.stringify({ protocol: "json", version: 1 }) + recordSeparator);
        });

        socket.addEventListener("message", event => this.receive(event.data));
        socket.addEventListener("error", event => {
          if (!this.handshakeComplete) {
            reject(event);
          }
        });
        socket.addEventListener("close", () => {
          if (!this.handshakeComplete) {
            reject(new Error("SignalR connection closed before handshake completed."));
          }

          this.state = "Disconnected";
          this.closeHandlers.forEach(handler => handler());
          this.scheduleReconnect();
        });
      });
    }

    receive(data) {
      for (const rawMessage of String(data).split(recordSeparator)) {
        if (!rawMessage) {
          continue;
        }

        const message = JSON.parse(rawMessage);
        if (!this.handshakeComplete && !message.type) {
          this.handshakeComplete = true;
          this.state = "Connected";
          this.handshakeResolver?.();
          this.handshakeResolver = null;
          continue;
        }

        if (message.type !== 1 || !message.target) {
          continue;
        }

        const handler = this.handlers.get(message.target);
        if (handler) {
          handler(...(message.arguments || []));
        }
      }
    }

    scheduleReconnect() {
      if (!this.reconnect || this.reconnectTimer) {
        return;
      }

      this.state = "Reconnecting";
      this.reconnectingHandlers.forEach(handler => handler());
      this.reconnectTimer = window.setTimeout(async () => {
        this.reconnectTimer = null;
        try {
          await this.start();
          this.reconnectedHandlers.forEach(handler => handler());
        } catch {
          this.scheduleReconnect();
        }
      }, 1500);
    }
  }

  function toWebSocketUrl(path) {
    const url = new URL(path, window.location.href);
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    return url.toString();
  }

  window.signalR = { HubConnectionBuilder };
})();
