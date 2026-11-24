import { SharedArray } from 'k6/data';
import { env } from './config.js';

// The fixture is written by tools/loadtest.mjs (docs: load-tests/README.md). The path is relative to this file.
const path = env('FIXTURE', '../.data/fixture.json');
const loaded = new SharedArray('fixture', () => [JSON.parse(open(path))]);

export const fixture = loaded[0];
export const users = fixture.users; // { index, orgIndex }
export const orgs = fixture.orgs; // { id, cellId, cellUrl, channelIds }

export function userFor(vu) {
  return users[(vu - 1) % users.length];
}

export function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

export function uuid() {
  const hex = () => Math.floor(Math.random() * 16).toString(16);
  const part = (n) => Array.from({ length: n }, hex).join('');
  return `${part(8)}-${part(4)}-4${part(3)}-${'89ab'[Math.floor(Math.random() * 4)]}${part(3)}-${part(12)}`;
}
