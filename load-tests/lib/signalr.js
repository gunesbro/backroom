import { WebSocket } from 'k6/websockets';

// A minimal SignalR client for the MessagePack protocol (the hub accepts nothing else, and only WebSockets). It
// encodes the few invocations a client sends (handshake, SubscribeConversation, ping) and does not decode the live events:
// the sender writes its send time into the message text ("lt:<epoch ms>:"), and the client finds that stamp in the
// frame bytes. This measures delivery through the whole path (live event, Valkey, SignalR) without a decoder.
const RECORD_SEPARATOR = '\u001e';
const STAMP = [0x6c, 0x74, 0x3a]; // "lt:"

function frame(payload) {
  const length = [];
  let n = payload.length;
  do {
    let b = n & 0x7f;
    n >>>= 7;
    if (n > 0) b |= 0x80;
    length.push(b);
  } while (n > 0);
  return new Uint8Array([...length, ...payload]).buffer;
}

const PING = frame([0x91, 0x06]); // [6] = Ping

function encode(value, out) {
  if (value === null) {
    out.push(0xc0);
  } else if (typeof value === 'number') {
    if (!Number.isInteger(value) || value < 0 || value > 127) throw new Error('only small integers are needed');
    out.push(value);
  } else if (typeof value === 'string') {
    const bytes = Array.from(value, (c) => c.charCodeAt(0)); // ASCII only (ids and method names)
    if (bytes.length < 32) out.push(0xa0 | bytes.length);
    else if (bytes.length < 256) out.push(0xd9, bytes.length);
    else throw new Error('string too long');
    out.push(...bytes);
  } else if (Array.isArray(value)) {
    if (value.length > 15) throw new Error('array too long');
    out.push(0x90 | value.length);
    value.forEach((item) => encode(item, out));
  } else {
    out.push(0x80); // empty map (message headers)
  }
}

/** Invocation [1, {}, invocationId, target, args]; with an id the server answers with a completion. */
function invocation(target, args, invocationId = null) {
  const out = [];
  encode([1, {}, invocationId, target, args], out);
  return frame(out);
}

/**
 * The completions in a binary WebSocket message, as [invocationId, ok]. A message can hold several length-prefixed
 * SignalR messages; a completion is [3, {}, invocationId, resultKind, ...] with resultKind 1 for an error.
 */
export function completionsOf(buffer) {
  const bytes = new Uint8Array(buffer);
  const found = [];
  let offset = 0;
  while (offset < bytes.length) {
    let length = 0;
    let shift = 0;
    let b;
    do {
      b = bytes[offset++];
      length |= (b & 0x7f) << shift;
      shift += 7;
    } while (b & 0x80 && offset < bytes.length);
    const start = offset;
    offset += length;
    // fixarray, type 3 (Completion), empty header map, fixstr invocation id, then the result kind.
    if ((bytes[start] & 0xf0) === 0x90 && bytes[start + 1] === 3 && bytes[start + 2] === 0x80 && (bytes[start + 3] & 0xe0) === 0xa0) {
      const idLength = bytes[start + 3] & 0x1f;
      const id = String.fromCharCode(...bytes.subarray(start + 4, start + 4 + idLength));
      found.push([id, bytes[start + 4 + idLength] !== 1]);
    }
  }
  return found;
}

/**
 * The send times of every "lt:<ms>:" stamp in a binary frame. The server may write several hub messages into one
 * WebSocket frame when they are sent together (a burst after a slow commit), so a frame can carry more than one.
 */
export function stampsOf(buffer) {
  const bytes = new Uint8Array(buffer);
  const stamps = [];
  for (let i = 0; i + 4 < bytes.length; i++) {
    if (bytes[i] === STAMP[0] && bytes[i + 1] === STAMP[1] && bytes[i + 2] === STAMP[2]) {
      let ms = 0;
      let j = i + 3;
      while (j < bytes.length && bytes[j] >= 0x30 && bytes[j] <= 0x39) {
        ms = ms * 10 + (bytes[j] - 0x30);
        j++;
      }
      if (j > i + 3 && bytes[j] === 0x3a) {
        stamps.push(ms);
        i = j;
      }
    }
  }
  return stamps;
}

/**
 * Opens the hub, subscribes to the channels and calls onVisible(milliseconds since the stamp) for each message posted.
 * onReady(milliseconds from connecting until the server confirmed every subscription) is called once, and only if none
 * was refused; a refused subscription calls onError('subscribe') instead. The connection closes after holdMs.
 * The k6 iteration ends when the connection has closed (the event loop is empty).
 */
export function listen({ cellUrl, token, orgId, channelIds, holdMs, onReady, onVisible, onError }) {
  const url = `${cellUrl.replace(/^http/, 'ws').replace(/\/$/, '')}/hubs/realtime?access_token=${encodeURIComponent(token)}`;
  const started = Date.now();
  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  let pinger;
  let handshakeDone = false;
  let pending = 0;
  let refused = false;

  ws.onopen = () => ws.send(`{"protocol":"messagepack","version":1}${RECORD_SEPARATOR}`);
  ws.onmessage = (event) => {
    if (!handshakeDone) {
      // The handshake answer ("{}" or an error, JSON ending with the record separator). With a binary protocol the server
      // sends it in a binary frame, so it is read as text whatever the frame type.
      handshakeDone = true;
      const answer = typeof event.data === 'string' ? event.data : String.fromCharCode(...new Uint8Array(event.data));
      if (answer.startsWith('{"error"')) {
        onError('handshake');
        ws.close();
        return;
      }
      pending = channelIds.length;
      channelIds.forEach((id, i) => ws.send(invocation('SubscribeConversation', [orgId, id], String(i + 1))));
      pinger = setInterval(() => {
        if (ws.readyState === 1) ws.send(PING);
      }, 10_000);
      setTimeout(() => {
        clearInterval(pinger);
        ws.close();
      }, holdMs);
      return;
    }
    if (pending > 0) {
      for (const [, ok] of completionsOf(event.data)) {
        if (!ok) {
          refused = true;
          onError('subscribe');
        }
        if (--pending === 0 && !refused) onReady(Date.now() - started);
      }
    }
    for (const sentAt of stampsOf(event.data)) {
      const elapsed = Date.now() - sentAt;
      if (elapsed >= 0 && elapsed < 60_000) onVisible(elapsed);
    }
  };
  ws.onerror = () => onError('socket');
  ws.onclose = () => {
    if (pinger) clearInterval(pinger);
  };
}
