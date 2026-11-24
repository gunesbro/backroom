import { config } from '@/config';
import { getJson, type AccessTokenSource } from '@/shared/http';

export type Cell = {
  id: string;
  type: string;
  region: string;
  /** `pending` until the cell has registered, then `registered`. */
  status: string;
  version: string | null;
  linkStatus: 'connected' | 'disconnected';
  lastHeartbeatAt: string | null;
  organizationCount: number;
  pendingMessages: number;
  rejectedMessages: number;
};

type CellPage = { cells: Cell[]; next: string | null };

/** The control plane serves at most 1,000 cells a page; a few pages are far more than the product has. */
const MAX_PAGES = 5;

export const cellsQueryKey = ['ops', 'cells'] as const;

/** The cell list, following the cursor of each page. */
export async function fetchCells(getAccessToken: AccessTokenSource, signal?: AbortSignal): Promise<Cell[]> {
  const cells: Cell[] = [];
  let after: string | null = null;
  for (let page = 0; page < MAX_PAGES; page++) {
    const query: string = after === null ? '' : `?after=${encodeURIComponent(after)}`;
    const result: CellPage = await getJson<CellPage>(
      `${config.controlPlaneUrl}/api/v1/ops/cells${query}`,
      getAccessToken,
      signal,
    );
    cells.push(...result.cells);
    after = result.next;
    if (after === null) break;
  }

  return cells;
}
