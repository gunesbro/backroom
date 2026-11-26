import { createContext } from 'react';
import { createRealtimeClient, type HubFactory, type RealtimeClient } from '@backroom/realtime';
import { cellOrigin, LOOPBACK_HTTP_CELLS, type Cell, type CellTokenSource } from './api';

/** Builds the live connection of one cell; the app provides it, tests provide fakes. */
export type RealtimeFactory = (cell: Cell) => RealtimeClient;

/** Null while live updates are off (messaging is off, or no cell token source): then no connection is made. */
export const RealtimeContext = createContext<RealtimeFactory | null>(null);

const nothing = () => undefined;

/**
 * The live connection of a refused cell address: it never starts, so no token is asked for, no connection is
 * opened and no retry runs; its status stays `stopped`. The HTTP API refuses the same address on its own.
 */
const REFUSED_CLIENT: RealtimeClient = {
  start: nothing,
  stop: () => Promise.resolve(),
  subscribeOrganization: nothing,
  unsubscribeOrganization: nothing,
  subscribeConversation: nothing,
  unsubscribeConversation: nothing,
  watchPresence: nothing,
  reportActivity: nothing,
  on: () => nothing,
  onStatus: () => nothing,
  status: () => 'stopped',
};

/**
 * The hub of a cell, with a token made for that cell (the same source the HTTP API uses). The library loads on connect.
 * The cell address goes through the same rule as the HTTP API (`cellOrigin`): a refused address gets a client that
 * never connects. Tests inject the hub; it is used only for an allowed address.
 */
export function createRealtimeFactory(
  cellTokens: CellTokenSource,
  {
    allowLoopbackHttp = LOOPBACK_HTTP_CELLS,
    hubFactory,
  }: { allowLoopbackHttp?: boolean; hubFactory?: HubFactory } = {},
): RealtimeFactory {
  return (cell) => {
    const origin = cellOrigin(cell.url, allowLoopbackHttp);
    if (!origin) return REFUSED_CLIENT;
    return createRealtimeClient({
      cellUrl: origin,
      getAccessToken: () => cellTokens(cell.id),
      ...(hubFactory ? { hubFactory } : {}),
    });
  };
}
