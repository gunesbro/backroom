import type { QueryClient } from '@tanstack/react-query';
import { writeItem } from '@/shared/storage';

/** Ids only, never names: the organization being created (this tab) and the last one opened (this browser). */
export const PENDING_ORGANIZATION_KEY = 'ph.organizations.pending';
export const LAST_ORGANIZATION_KEY = 'ph.organizations.last';

export const MY_ORGANIZATIONS = ['me', 'organizations'] as const;

/**
 * Opens an organization the account has just joined (by invitation): the cached list without it is dropped, so the
 * gate loads the list again and opens it.
 */
export function openOrganization(queryClient: QueryClient, id: string): void {
  queryClient.removeQueries({ queryKey: MY_ORGANIZATIONS });
  writeItem('local', LAST_ORGANIZATION_KEY, id);
}
