import { vi } from 'vitest';
import type { AuthClient } from '@/features/auth';
import type { OrganizationsApi } from '@/features/organizations';

export function fakeAuthClient(overrides: Partial<AuthClient> = {}): AuthClient {
  return {
    start: vi.fn(() =>
      Promise.resolve({ email: 'alice@example.test', name: 'Alice Example', givenName: 'Alice' }),
    ),
    signIn: vi.fn(() => Promise.resolve()),
    getAccessToken: vi.fn(() => Promise.resolve('token')),
    signOut: vi.fn(() => Promise.resolve()),
    ...overrides,
  };
}

export function fakeOrganizationsApi(overrides: Partial<OrganizationsApi> = {}): OrganizationsApi {
  return {
    listMine: vi.fn(() => Promise.resolve([])),
    create: vi.fn((input: { name: string; region: string }) =>
      Promise.resolve({
        id: `id-${input.name}`,
        status: 'provisioning' as const,
        cellId: 'a',
        cellUrl: 'https://a.example.test',
      }),
    ),
    getState: vi.fn((id: string) => Promise.resolve({ id, status: 'active' as const, code: null })),
    ...overrides,
  };
}

export function organization(id: string, name: string) {
  return { id, name, cellId: 'a', cellUrl: 'https://a.example.test' };
}
