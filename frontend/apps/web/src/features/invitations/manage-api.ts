import { createContext, useContext } from 'react';
import { cellOrigin, LOOPBACK_HTTP_CELLS, type Cell, type CellTokenSource } from '@/features/messaging';
import { requestJson, type AccessTokenSource } from '@/shared/http';

export type Role = 'admin' | 'member';
/** A pending invitation as the cell lists it (no code: that exists only in the email). */
export type Invitation = {
  id: string;
  email: string;
  role: Role;
  createdBy: string;
  createdAt: string;
  expiresAt: string;
};
export type InvitationPage = { invitations: Invitation[]; next: string | null };

/** Page size asked for; the server allows 1 to 100. */
export const INVITATION_PAGE_SIZE = 50;

/** A cell's invitation endpoints (docs/contracts/phase-1.md, "HTTP APIs", Cell). */
export interface InvitationsApi {
  /** One invitation per address; `role` only when the person chooses one (the default is Member). */
  create: (orgId: string, input: { email: string; role?: Role }) => Promise<Invitation>;
  list: (orgId: string, after: string | undefined, signal?: AbortSignal) => Promise<InvitationPage>;
  revoke: (orgId: string, invitationId: string) => Promise<void>;
  resend: (orgId: string, invitationId: string) => Promise<Invitation>;
}

export type InvitationsApiFactory = (cell: Cell) => InvitationsApi;

export function createInvitationsApi(
  cellUrl: string,
  cellToken: AccessTokenSource,
  { allowLoopbackHttp = LOOPBACK_HTTP_CELLS }: { allowLoopbackHttp?: boolean } = {},
): InvitationsApi {
  const origin = cellOrigin(cellUrl, allowLoopbackHttp);
  // Every request asks for its token first, so a refused address never receives one.
  const getAccessToken: AccessTokenSource = origin
    ? cellToken
    : () => Promise.reject(new Error('The cell address is not allowed.'));
  const base = (orgId: string) =>
    `${origin ?? 'about:blank'}/api/v1/orgs/${encodeURIComponent(orgId)}/invitations`;
  const one = (orgId: string, id: string) => `${base(orgId)}/${encodeURIComponent(id)}`;
  return {
    create: (orgId, input) => requestJson(base(orgId), getAccessToken, { method: 'POST', body: input }),
    list: (orgId, after, signal) => {
      const query = new URLSearchParams({ limit: String(INVITATION_PAGE_SIZE) });
      if (after) query.set('after', after);
      return requestJson(`${base(orgId)}?${query.toString()}`, getAccessToken, { signal });
    },
    revoke: (orgId, id) => requestJson(one(orgId, id), getAccessToken, { method: 'DELETE' }),
    resend: (orgId, id) => requestJson(`${one(orgId, id)}/resend`, getAccessToken, { method: 'POST' }),
  };
}

export function createInvitationsApiFactory(cellToken: CellTokenSource): InvitationsApiFactory {
  return (cell) => createInvitationsApi(cell.url, () => cellToken(cell.id));
}

/** Null while the invitation UI is off (the feature flag): then no entry is offered and no cell is called. */
export const InvitationsApiContext = createContext<InvitationsApiFactory | null>(null);

export function useInvitationsEnabled(): boolean {
  return useContext(InvitationsApiContext) !== null;
}

export function useInvitationsApiFactory(): InvitationsApiFactory {
  const factory = useContext(InvitationsApiContext);
  if (!factory) throw new Error('Invitations are used outside InvitationsApiContext.');
  return factory;
}
