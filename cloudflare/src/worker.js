import {DurableObject} from 'cloudflare:workers';
import {RoomState} from './room.js';

const json = (body, status = 200) => Response.json(body, {status, headers: {'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff'}});
async function matches(a, b) {
  if (!b || a.length > 512) return false;
  const enc = new TextEncoder();
  const [x, y] = await Promise.all([a, b].map(v => crypto.subtle.digest('SHA-256', enc.encode(v))));
  const ax = new Uint8Array(x), ay = new Uint8Array(y);
  let diff = 0; for (let i = 0; i < 32; i++) diff |= ax[i] ^ ay[i]; return diff === 0;
}
export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/health') return json({service: 'DriftStart', version: '0.2.0', transport: 'websocket'});
    if (url.pathname !== '/ws' || request.method !== 'GET') return json({error: 'Not found'}, 404);
    if (env.DRIFT_ROOM_CODE?.length < 6 || env.DRIFT_JUDGE_KEY?.length < 16 || !env.DRIFT_ROOM_CODE || !env.DRIFT_JUDGE_KEY)
      return json({error: 'Сервер ещё не настроен'}, 503);
    if (!await matches(request.headers.get('X-Room-Code') || '', env.DRIFT_ROOM_CODE)) return json({error: 'Неверный код комнаты'}, 401);
    const role = request.headers.get('X-Client-Role') === 'judge' ? 'judge' : 'player';
    if (role === 'judge' && !await matches(request.headers.get('X-Judge-Key') || '', env.DRIFT_JUDGE_KEY)) return json({error: 'Неверный ключ судьи'}, 403);
    if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket') return json({error: 'WebSocket required'}, 426);
    const headers = new Headers(request.headers); headers.set('X-Verified-Role', role);
    return env.ROOM.getByName('main').fetch(new Request(request, {headers}));
  }
};

export class DriftRoom extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env); this.ctx = ctx;
    ctx.blockConcurrencyWhile(async () => { this.room = new RoomState(await ctx.storage.get('room')); });
  }
  connections() { return this.ctx.getWebSockets().filter(s => s.readyState === WebSocket.OPEN); }
  peers() { return this.connections().map(s => s.deserializeAttachment()).filter(p => p.role === 'player'); }
  send(socket, value) { try { socket.send(JSON.stringify(value)); } catch { try { socket.close(1011, 'Reconnect'); } catch {} } }
  state(socket) { const p = socket.deserializeAttachment(); return this.room.snapshot(this.peers(), p.id, Date.now()); }
  broadcast() { for (const s of this.connections()) this.send(s, this.state(s)); }
  async fetch(request) {
    const role = request.headers.get('X-Verified-Role');
    if (!['player', 'judge'].includes(role)) return json({error: 'Forbidden'}, 403);
    if (this.connections().length >= 68 || (role === 'player' && this.peers().length >= 64)) return json({error: 'Комната заполнена'}, 429);
    let name = 'Пилот';
    try { name = new TextDecoder().decode(Uint8Array.from(atob(request.headers.get('X-Client-Name') || ''), c => c.charCodeAt(0))).trim().slice(0, 32) || name; } catch {}
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment({id: crypto.randomUUID(), name, role, seen: Date.now(), readyEpoch: -1, ack: -1, bucket: Date.now(), messages: 0});
    this.broadcast();
    return new Response(null, {status: 101, webSocket: client});
  }
  async webSocketMessage(socket, data) {
    const p = socket.deserializeAttachment(), now = Date.now();
    if (typeof data !== 'string' || data.length > 4096) { socket.close(1009, 'Message too large'); return; }
    if (now - p.bucket > 1000) { p.bucket = now; p.messages = 0; }
    if (++p.messages > 16) { socket.close(1008, 'Message rate exceeded'); return; }
    p.seen = now;
    let m; try { m = JSON.parse(data); } catch { socket.close(1008, 'Invalid JSON'); return; }
    if (!m || typeof m !== 'object') { socket.close(1008, 'Invalid message'); return; }
    if (m.type === 'ping') {
      socket.serializeAttachment(p);
      this.send(socket, {type: 'pong', nonce: m.nonce, serverMs: now, state: this.state(socket)});
      return;
    }
    try {
      if (m.type === 'ack') {
        p.ack = m.version === this.room.version ? m.version : -1;
        socket.serializeAttachment(p); this.broadcast();
      } else if (m.type === 'ready') {
        if (p.role !== 'player') throw Error('Готовность отмечают пилоты');
        if (this.room.active(now)) throw Error('Дождитесь завершения отсчёта');
        p.readyEpoch = m.ready && m.epoch === this.room.readyEpoch ? this.room.readyEpoch : -1;
        socket.serializeAttachment(p); this.broadcast();
      } else if (m.type === 'control') {
        if (p.role !== 'judge') throw Error('Команда доступна только судье');
        socket.serializeAttachment(p);
        this.room.control(m, this.peers(), now);
        if (m.action === 'prune') for (const s of this.connections()) {
          if (now - s.deserializeAttachment().seen > 3000) s.close(1000, 'Inactive participant removed');
        }
        await this.ctx.storage.put('room', {...this.room});
        this.broadcast(); this.send(socket, {type: 'result', requestId: m.requestId, action: m.action, ok: true});
      } else throw Error('Неизвестный тип сообщения');
    } catch (e) { this.send(socket, {type: 'error', requestId: m.requestId, error: e.message}); }
  }
  async webSocketClose(socket, code) { try { socket.close(code === 1006 ? 1000 : code); } catch {} this.broadcast(); }
  async webSocketError(socket) { try { socket.close(1011, 'Connection error'); } catch {} this.broadcast(); }
}
