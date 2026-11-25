import { createContext, useContext } from 'react';
import type { CustomStatus, PresenceState } from '@backroom/realtime';
import { cellOrigin } from '@/features/messaging';
import { requestJson, type AccessTokenSource } from '@/shared/http';

/** The person's own status in the control plane (docs/contracts/phase-2.md, flow 9); expired parts come back null. */
export type AccountStatus = {
  dndUntil: string | null;
  /** Away chosen by hand: no end, never together with do not disturb. Others only see the same "idle" as the automatic one. */
  away: boolean;
  customStatus: CustomStatus | null;
  timeZone: string | null;
  version: number;
};

/** A whole status: PUT replaces everything, so every change sends the parts it keeps too. */
export type StatusInput = {
  dndUntil: string | null;
  away: boolean;
  customStatus: CustomStatus | null;
  timeZone: string;
};

/** Another member's presence, as the cell answers it (state and status only; never platform, device or time zone). */
export type MemberPresence = {
  accountId: string;
  state: PresenceState;
  dndUntil: string | null;
  customStatus: CustomStatus | null;
};

/** The most people one presence request may ask about. */
export const MAX_PRESENCE_IDS = 200;

export interface StatusApi {
  get: (signal?: AbortSignal) => Promise<AccountStatus>;
  put: (input: StatusInput) => Promise<AccountStatus>;
}

/** The control plane's `/api/v1/me/status` (contract, "Control plane"). */
export function createStatusApi(baseUrl: string, getAccessToken: AccessTokenSource): StatusApi {
  const url = `${baseUrl}/api/v1/me/status`;
  return {
    get: (signal) => requestJson(url, getAccessToken, { signal }),
    put: (input) => requestJson(url, getAccessToken, { method: 'PUT', body: input }),
  };
}

/** Presence of members of one organization, from its cell (`GET /presence?accountIds=`); non-members are left out. */
export async function listPresence(
  cellUrl: string,
  cellToken: AccessTokenSource,
  orgId: string,
  accountIds: readonly string[],
  {
    signal,
    allowLoopbackHttp = import.meta.env.DEV,
  }: { signal?: AbortSignal; allowLoopbackHttp?: boolean } = {},
): Promise<MemberPresence[]> {
  const ids = [...new Set(accountIds)].slice(0, MAX_PRESENCE_IDS);
  if (ids.length === 0) return [];
  const origin = cellOrigin(cellUrl, allowLoopbackHttp);
  if (!origin) throw new Error('The cell address is not allowed.');
  const query = new URLSearchParams({ accountIds: ids.join(',') });
  const page = await requestJson<{ presence: MemberPresence[] }>(
    `${origin}/api/v1/orgs/${encodeURIComponent(orgId)}/presence?${query.toString()}`,
    cellToken,
    { signal },
  );
  return page.presence;
}

/** Null while presence is off (the feature flag): the status menu stays passive and no activity is reported. */
export const StatusApiContext = createContext<StatusApi | null>(null);

export function usePresenceEnabled(): boolean {
  return useContext(StatusApiContext) !== null;
}

export function useStatusApi(): StatusApi {
  const api = useContext(StatusApiContext);
  if (!api) {
    throw new Error('useStatusApi is used outside StatusApiContext.');
  }

  return api;
}
