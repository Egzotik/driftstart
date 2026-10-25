export class RoomState {
  constructor(saved) {
    Object.assign(this, saved ?? {instance: crypto.randomUUID(), version: 0, readyEpoch: 0, label: 'Свободный заезд', startAt: null, countdownMs:6000, seats: []});
  }
  active(now) { return this.startAt !== null && now < this.startAt + 3000; }
  control(command, peers, now) {
    const action = command.action;
    if (action === 'start') {
      if (this.active(now)) throw Error('Предыдущий старт ещё активен');
      const delay = command.delayMs ?? 6000;
      if (!Number.isInteger(delay) || delay < 1000 || delay > 60000) throw Error('Отсчёт: от 1 до 60 секунд');
      this.startAt = now + delay;
      this.countdownMs = delay;
      this.label = String(command.label || 'Свободный заезд').trim().slice(0, 48);
      // Readiness is a judge-facing hint, not a lock. This also allows an
      // empty-room HUD/sound check while preserving one authoritative start time.
      this.seats = peers.filter(p => now - p.seen <= 10000).map(p => p.id);
    } else if (action === 'cancel') {
      this.startAt = null; this.seats = [];
    } else if (action === 'prune') {
      if (this.active(now)) throw Error('Сначала отмените текущий старт');
    } else throw Error('Неизвестная команда');
    this.version++; this.readyEpoch++;
  }
  snapshot(peers, id, now) {
    return {type: 'state', serverMs: now, instance: this.instance, version: this.version,
      readyEpoch: this.readyEpoch, label: this.label, startAt: this.startAt, countdownMs:this.countdownMs,
      seated: this.seats.includes(id), peers: peers.map(p => ({id: p.id, name: p.name, userId:p.userId, username:p.username,nickname:p.nickname,staticId:p.staticId,vehicleName:p.vehicleName,vehicleImage:p.vehicleImage,
        ready: p.readyEpoch === this.readyEpoch, ageMs: Math.max(0, now - p.seen), received: p.ack === this.version}))};
  }
}
