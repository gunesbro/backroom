// node --test load-tests/tools
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { assertSafeTargets } from './targets.mjs';

const isolated = { KEYCLOAK_URL: 'https://localhost:49525', CONTROL_PLANE_URL: 'http://localhost:49529/', MAILPIT_URL: 'http://127.0.0.1:49519' };

test('accepts an isolated local stack', () => {
  assert.doesNotThrow(() => assertSafeTargets(isolated, {}));
});

test('refuses a target that is not set, instead of falling back to a default', () => {
  for (const missing of [undefined, null, '']) {
    assert.throws(() => assertSafeTargets({ ...isolated, CONTROL_PLANE_URL: missing }, {}), /CONTROL_PLANE_URL is not set/);
  }
});

test('refuses every development port', () => {
  for (const url of ['https://localhost:8443', 'http://localhost:5100', 'http://127.0.0.1:8025', 'http://[::1]:5202/', 'http://localhost:5200']) {
    assert.throws(() => assertSafeTargets({ ...isolated, KEYCLOAK_URL: url }, {}), /development stack/);
  }
});

test('allows the development stack only with the explicit override', () => {
  const development = { KEYCLOAK_URL: 'https://localhost:8443' };
  assert.throws(() => assertSafeTargets(development, { LOADTEST_ALLOW_REMOTE: 'yes' }), /development stack/);
  assert.doesNotThrow(() => assertSafeTargets(development, { LOADTEST_ALLOW_DEVELOPMENT_STACK: 'yes' }));
});

test('refuses a remote host unless allowed', () => {
  const remote = { ...isolated, KEYCLOAK_URL: 'https://id.loadtest.example' };
  assert.throws(() => assertSafeTargets(remote, {}), /non-local/);
  assert.doesNotThrow(() => assertSafeTargets(remote, { LOADTEST_ALLOW_REMOTE: 'yes' }));
});

test('refuses a value that is not a URL', () => {
  assert.throws(() => assertSafeTargets({ KEYCLOAK_URL: 'localhost' }, {}), /not a URL/);
});
