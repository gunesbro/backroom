import { vi } from 'vitest';
import type { RealtimeClient } from '@backroom/realtime';
import type { AuthClient } from '@/features/auth';
import type { RealtimeFactory } from '@/features/messaging';
import type { OrganizationsApi } from '@/features/organizations';

export function fakeAuthClient(overrides: Partial<AuthClient> = {}): AuthClient {
  return {
    start: vi.fn(() =>
      Promise.resolve({ email: 'alice@example.test', name: 'Alice Example', givenName: 'Alice' }),
    ),
    signIn: vi.fn(() => Promise.resolve()),
    // One token per target, so a test can tell which one a request carried.
    getAccessToken: vi.fn((target: string) =>
      Promise.resolve(target === 'control-plane' ? 'token' : `token-${target}`),
    ),
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

/** A live connection that never connects anywhere: app tests must not open sockets from jsdom. */
export function fakeRealtimeFactory(): RealtimeFactory {
  return () => {
    const client: RealtimeClient = {
      start: () => undefined,
      stop: () => Promise.resolve(),
      subscribeOrganization: () => undefined,
      unsubscribeOrganization: () => undefined,
      subscribeConversation: () => undefined,
      unsubscribeConversation: () => undefined,
      watchPresence: () => undefined,
      reportActivity: () => undefined,
      on: () => () => undefined,
      onStatus: () => () => undefined,
      status: () => 'connected',
    };
    return client;
  };
}
