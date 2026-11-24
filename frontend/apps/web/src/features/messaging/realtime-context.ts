import { createContext } from 'react';
import { createRealtimeClient, type RealtimeClient } from '@backroom/realtime';
import type { Cell, CellTokenSource } from './api';

/** Builds the live connection of one cell; the app provides it, tests provide fakes. */
export type RealtimeFactory = (cell: Cell) => RealtimeClient;

/** Null while live updates are off (messaging is off, or no cell token source): then no connection is made. */
export const RealtimeContext = createContext<RealtimeFactory | null>(null);

/** The hub of a cell, with a token made for that cell (the same source the HTTP API uses). The library loads on connect. */
export function createRealtimeFactory(cellTokens: CellTokenSource): RealtimeFactory {
  return (cell) => createRealtimeClient({ cellUrl: cell.url, getAccessToken: () => cellTokens(cell.id) });
}
