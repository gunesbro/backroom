// The part of a SignalR connection the client uses. Narrow on purpose: tests supply a fake, and the real library
// is loaded lazily so it stays out of the initial bundle.

export interface Hub {
  start: () => Promise<void>;
  stop: () => Promise<void>;
  invoke: (method: string, ...args: unknown[]) => Promise<unknown>;
  on: (event: string, handler: (payload: unknown) => void) => void;
  /** Called when the connection is closed: by the server, the network or `stop`. */
  onClose: (handler: () => void) => void;
}

/** Builds a hub for the cell's `/hubs/realtime` URL; the token is asked for on every connection attempt. */
export type HubFactory = (url: string, getAccessToken: () => Promise<string>) => Promise<Hub>;

/** WebSockets only and no negotiate request (the contract allows no other transport), MessagePack only. */
export const createSignalrHub: HubFactory = async (url, getAccessToken) => {
  const [signalr, { MessagePackHubProtocol }] = await Promise.all([
    import('@microsoft/signalr'),
    import('@microsoft/signalr-protocol-msgpack'),
  ]);
  const connection = new signalr.HubConnectionBuilder()
    .withUrl(url, {
      transport: signalr.HttpTransportType.WebSockets,
      skipNegotiation: true,
      accessTokenFactory: getAccessToken,
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .configureLogging(signalr.LogLevel.None)
    .build();
  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    invoke: (method, ...args) => connection.invoke<unknown>(method, ...args),
    on: (event, handler) => {
      connection.on(event, handler);
    },
    onClose: (handler) => {
      connection.onclose(() => {
        handler();
      });
    },
  };
};
