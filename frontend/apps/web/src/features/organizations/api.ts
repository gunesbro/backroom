import { createContext, useContext } from 'react';
import { requestJson, type AccessTokenSource } from '@/shared/http';

export type AccountOrganization = { id: string; name: string; cellId: string; cellUrl: string };
export type CreatedOrganization = { id: string; status: OrganizationStatus; cellId: string; cellUrl: string };
export type OrganizationStatus = 'provisioning' | 'active' | 'failed';
export type OrganizationState = { id: string; status: OrganizationStatus; code: string | null };

/** The control plane's organization endpoints (docs/contracts/phase-1.md, "Control plane API"). */
export interface OrganizationsApi {
  listMine: (signal?: AbortSignal) => Promise<AccountOrganization[]>;
  create: (input: { name: string; region: string }) => Promise<CreatedOrganization>;
  getState: (id: string, signal?: AbortSignal) => Promise<OrganizationState>;
}

export function createOrganizationsApi(baseUrl: string, getAccessToken: AccessTokenSource): OrganizationsApi {
  return {
    listMine: async (signal) =>
      (
        await requestJson<{ organizations: AccountOrganization[] }>(
          `${baseUrl}/api/v1/me/organizations`,
          getAccessToken,
          { signal },
        )
      ).organizations,
    create: (input) =>
      requestJson(`${baseUrl}/api/v1/organizations`, getAccessToken, { method: 'POST', body: input }),
    getState: (id, signal) =>
      requestJson(`${baseUrl}/api/v1/organizations/${encodeURIComponent(id)}`, getAccessToken, { signal }),
  };
}

export const OrganizationsApiContext = createContext<OrganizationsApi | null>(null);

export function useOrganizationsApi(): OrganizationsApi {
  const api = useContext(OrganizationsApiContext);
  if (!api) {
    throw new Error('useOrganizationsApi is used outside OrganizationsApiContext.');
  }

  return api;
}
