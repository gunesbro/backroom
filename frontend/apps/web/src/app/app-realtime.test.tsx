import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeAuthClient, fakeOrganizationsApi, organization } from '@/testing/fakes';
import { App } from './app';

// The live connection is replaced by a spy, so the test sees which token source it was given without opening a socket.
const realtimeTokenSources: ((cellId: string) => Promise<string>)[] = [];
vi.mock('@/features/messaging', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/features/messaging')>();
  return {
    ...original,
    createRealtimeFactory: (tokens: (cellId: string) => Promise<string>) => {
      realtimeTokenSources.push(tokens);
      return original.createRealtimeFactory(() => new Promise<string>(() => undefined));
    },
  };
});

afterEach(() => {
  realtimeTokenSources.length = 0;
});

describe('App live updates', () => {
  it('Organization_WithoutATestTokenSource_ConnectsLiveUpdatesWithTheSignedInCellToken', async () => {
    const authClient = fakeAuthClient();
    const api = fakeOrganizationsApi({ listMine: () => Promise.resolve([organization('1', 'Acme')]) });
    // No `cellTokens` prop: this is how the real app renders.
    render(<App authClient={authClient} createApi={() => api} messagingEnabled />);

    expect(await screen.findByRole('navigation')).toBeInTheDocument();
    await waitFor(() => {
      expect(realtimeTokenSources).not.toHaveLength(0);
    });
    await expect(realtimeTokenSources[0]?.('a')).resolves.toBe('token-cell:a');
  });
});
