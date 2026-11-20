import { InMemoryWebStorage, OidcClient, WebStorageStateStore } from 'oidc-client-ts';
import { describe, expect, it } from 'vitest';
import { isUnknownState } from './oidc-auth-client';

// Runs the real library (the other auth tests mock it), so an upgrade that changes this error fails here.
describe('isUnknownState', () => {
  it('Recognize_TheLibrarysErrorForAStateNotInStorage_ReturnsTrue', async () => {
    const client = new OidcClient({
      authority: 'https://auth.example.test/realms/test',
      client_id: 'web',
      redirect_uri: 'http://localhost/',
      stateStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    });

    const error = await client.readSigninResponseState('http://localhost/?code=code-1&state=not-ours').then(
      () => undefined,
      (caught: unknown) => caught,
    );

    expect(isUnknownState(error)).toBe(true);
  });

  it('Recognize_AnyOtherError_ReturnsFalse', () => {
    expect(isUnknownState(new Error('access_denied'))).toBe(false);
    expect(isUnknownState('No matching state')).toBe(false);
  });
});
