import { requestJson, type AccessTokenSource } from '@/shared/http';

export type ResolvedInvitation = { organizationName: string; expiresAt: string; matchesAccount: boolean };
export type AcceptanceStatus = 'accepting' | 'used' | 'failed';
export type AcceptedInvitation = { organizationId: string; status: AcceptanceStatus };
export type Acceptance = { organizationId: string; status: AcceptanceStatus; code: string | null };

/**
 * The control plane's invitation endpoints (docs/contracts/phase-1.md, "Control plane API"). The code goes only in
 * POST bodies, never in a URL.
 */
export interface InvitationsApi {
  resolve: (code: string, signal?: AbortSignal) => Promise<ResolvedInvitation>;
  accept: (code: string) => Promise<AcceptedInvitation>;
  /** The signed-in account's latest acceptance; takes no code. */
  getAcceptance: (signal?: AbortSignal) => Promise<Acceptance>;
}

export function createInvitationsApi(baseUrl: string, getAccessToken: AccessTokenSource): InvitationsApi {
  return {
    resolve: (code, signal) =>
      requestJson(`${baseUrl}/api/v1/invitations/resolve`, getAccessToken, {
        method: 'POST',
        body: { code },
        signal,
      }),
    accept: (code) =>
      requestJson(`${baseUrl}/api/v1/invitations/accept`, getAccessToken, { method: 'POST', body: { code } }),
    getAcceptance: (signal) =>
      requestJson(`${baseUrl}/api/v1/invitations/acceptance`, getAccessToken, { signal }),
  };
}
