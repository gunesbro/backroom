import { createContext } from 'react';
import { createRealtimeClient, type HubFactory, type RealtimeClient } from '@backroom/realtime';
import { CellAddressError, cellOrigin, LOOPBACK_HTTP_CELLS, type Cell, type CellTokenSource } from './api';

/** Builds the live connection of one cell; the app provides it, tests provide fakes. */
export type RealtimeFactory = (cell: Cell) => RealtimeClient;

/** Null while live updates are off (messaging is off, or no cell token source): then no connection is made. */
export const RealtimeContext = createContext<RealtimeFactory | null>(null);

/** Never contacted: the stand-in address of a refused cell, whose hub is never built. */
const REFUSED_URL = 'https://refused.invalid';

/**
 * The hub of a cell, with a token made for that cell (the same source the HTTP API uses). The library loads on connect.
 * The cell address goes through the same rule as the HTTP API (`cellOrigin`): a refused address never receives a
 * token, and no connection to it is opened.
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
    if (!origin) {
      const refuse = () => Promise.reject(new CellAddressError());
      return createRealtimeClient({ cellUrl: REFUSED_URL, getAccessToken: refuse, hubFactory: refuse });
    }
    return createRealtimeClient({
      cellUrl: origin,
      getAccessToken: () => cellTokens(cell.id),
      ...(hubFactory ? { hubFactory } : {}),
    });
  };
}
