import http from 'k6/http';
import { authHeaders } from './tokens.js';
import { uuid } from './fixture.js';

const base = (org) => `${org.cellUrl.replace(/\/$/, '')}/api/v1/orgs/${org.id}`;

/** The message text carries its send time, so a listener on the same machine can compute how long it took to arrive. */
export function stampedText(length) {
  return `lt:${Date.now()}:`.padEnd(Math.max(length, 20), 'x');
}

export function sendMessage(org, channelId, token, textLength) {
  const body = JSON.stringify({ clientMessageId: uuid(), text: stampedText(textLength) });
  return http.post(`${base(org)}/conversations/${channelId}/messages`, body, {
    headers: authHeaders(token),
    tags: { kind: 'send' },
  });
}

/** Newest first. `before` is the `next` of the previous page. */
export function historyPage(org, channelId, token, { before, after, limit }) {
  const query = [`limit=${limit}`];
  if (before !== undefined && before !== null) query.push(`before=${before}`);
  if (after !== undefined && after !== null) query.push(`after=${after}`);
  return http.get(`${base(org)}/conversations/${channelId}/messages?${query.join('&')}`, {
    headers: authHeaders(token),
    tags: { kind: 'history' },
  });
}
