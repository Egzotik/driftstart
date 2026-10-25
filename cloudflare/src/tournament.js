export function requireJudge(user) {
  if (!['owner', 'judge'].includes(user.role)) throw Error('Нужна роль судьи');
}
export function standings(t) {
  return t.entries.map(p => {
    const scores = p.scores.filter(v => v !== null).sort((a,b) => b-a);
    return {...p, best: scores[0] ?? null, second: scores[1] ?? null};
  }).sort((a,b) => (b.best ?? -1)-(a.best ?? -1) || (b.second ?? -1)-(a.second ?? -1) || a.order-b.order)
    .map((p,i) => ({...p, rank:i+1}));
}
export function newTournament(name, user, now=Date.now()) {
  requireJudge(user);
  if (typeof name !== 'string' || !name.trim() || name.length>64) throw Error('Название: от 1 до 64 символов');
  return {id:crypto.randomUUID(), name:name.trim(), createdAt:now, updatedAt:now, revision:0,
    phase:'qualification', entries:[], rounds:[], champion:null,
    audit:[{at:now, actor:user.id, name:user.name, action:'create', detail:'Турнир создан'}]};
}
function advance(t) {
  for (let r=0;r<t.rounds.length;r++) for (let i=0;i<t.rounds[r].length;i++) {
    const m=t.rounds[r][i];
    if(r>0) {
      const a=t.rounds[r-1][i*2],b=t.rounds[r-1][i*2+1];
      if(!a.done || !b.done) {m.a=a.done?a.winner:null;m.b=b.done?b.winner:null;continue;}
      m.a=a.winner;m.b=b.winner;
    }
    if(!m.done && (!m.a || !m.b)) {m.winner=m.a||m.b||null;m.done=true;m.bye=true;}
  }
  const last=t.rounds.at(-1)?.[0];
  if(last?.done) {t.champion=last.winner;t.phase='completed';}
}
export function mutateTournament(original, command, user, accounts, now=Date.now()) {
  // Work on a copy: rejected commands cannot leave partial in-memory mutations.
  const t=structuredClone(original), c=command;
  if(c.revision!==t.revision) throw Error('Данные обновились. Обновите турнир и повторите действие');
  if(t.phase==='archived') throw Error('Турнир в архиве: изменения закрыты');
  let detail='';
  if(c.action==='manualJoin') {
    requireJudge(user);if(t.phase!=='qualification')throw Error('Регистрация закрыта');const name=String(c.name||'').trim(),staticId=String(c.staticId||'').trim(),vehicleName=String(c.vehicleName||'').trim();if(name.length<2||name.length>32||!/^\d{1,10}$/.test(staticId)||vehicleName.length<2||vehicleName.length>64)throw Error('Укажите имя, статик и автомобиль');if(t.entries.length>=64)throw Error('Лимит: 64 участника');const id='manual-'+crypto.randomUUID();t.entries.push({id,name,staticId,vehicleName,vehicleImage:null,scores:[null,null],order:t.entries.length,manual:true});detail=`Ручная регистрация: ${name} #${staticId}`;
  } else if(c.action==='join') {
    if(t.phase!=='qualification') throw Error('Регистрация закрыта');
    const id=c.userId||user.id;
    if(id!==user.id) requireJudge(user);
    const account=accounts.find(a=>a.id===id);if(!account) throw Error('Участник должен сначала войти через Discord');
    if(t.entries.some(p=>p.id===id)) throw Error('Участник уже зарегистрирован');
    if(t.entries.length>=64) throw Error('Лимит: 64 участника');
    const p=account.profile;t.entries.push({id,name:p?.nickname||account.name,staticId:p?.staticId||null,vehicleName:p?.vehicleName||null,vehicleImage:p?.vehicleImage||null,scores:[null,null],order:t.entries.length});detail=`Регистрация: ${p?.nickname||account.name} (${id})`;
  } else {
    requireJudge(user);
    if(c.action==='score') {
      if(t.phase!=='qualification') throw Error('После создания сетки квалификация закрыта');
      const p=t.entries.find(p=>p.id===c.userId);
      if(!p || ![0,1].includes(c.attempt)) throw Error('Выберите участника и попытку');
      if(typeof c.score!=='number'||!Number.isFinite(c.score)||c.score<0||c.score>100||Math.abs(c.score*100-Math.round(c.score*100))>1e-6) throw Error('Оценка: 0–100, максимум два знака после запятой');
      p.judgeScores??=[{},{}];const own=p.judgeScores[c.attempt]??={};const old=own[user.id]??null;
      if(old!==null && (typeof c.reason!=='string'||c.reason.trim().length<3)) throw Error('Для исправления оценки укажите причину');
      own[user.id]=c.score;p.scores[c.attempt]=Object.values(own).reduce((sum,value)=>sum+value,0);detail=`${p.name}: судья ${user.name}, попытка ${c.attempt+1}, ${old??'—'} → ${c.score}; сумма ${p.scores[c.attempt]}`;
    } else if(c.action==='bracket') {
      if(t.phase!=='qualification') throw Error('Сетка уже создана');
      if(![2,4,8,16,32,64].includes(c.size)) throw Error('Размер сетки: 2, 4, 8, 16, 32 или 64');
      const ranked=standings(t);
      if(ranked.length<2 || ranked.some(p=>p.scores.includes(null))) throw Error('Поставьте обе оценки всем участникам (0 за незачёт)');
      // Equal scores use qualification registration order, explicitly displayed in the UI.
      let seeds=[1,2];while(seeds.length<c.size){const n=seeds.length*2+1;seeds=seeds.flatMap(s=>[s,n-s]);}
      const selected=ranked.slice(0,c.size);
      t.rounds=[Array.from({length:c.size/2},(_,i)=>({id:`0-${i}`,a:selected[seeds[i*2]-1]?.id??null,b:selected[seeds[i*2+1]-1]?.id??null,winner:null,done:false,bye:false}))];
      while(t.rounds.at(-1).length>1){const r=t.rounds.length;t.rounds.push(Array.from({length:t.rounds.at(-1).length/2},(_,i)=>({id:`${r}-${i}`,a:null,b:null,winner:null,done:false,bye:false})));}
      t.phase='bracket';advance(t);detail=`Создана сетка TOP ${c.size}, участников: ${selected.length}`;
    } else if(c.action==='clearWinner') {
      if(!['bracket','completed'].includes(t.phase)) throw Error('Сначала создайте сетку');
      const match=t.rounds.flat().find(m=>m.id===c.matchId);
      if(!match?.done||match.bye) throw Error('У этой пары нет результата для отмены');
      if(typeof c.reason!=='string'||c.reason.trim().length<3)throw Error('Укажите причину отмены результата');
      const [r,i]=match.id.split('-').map(Number);
      if(t.rounds[r+1]?.[Math.floor(i/2)].done)throw Error('Сначала отмените результат следующей пары');
      match.done=false;match.winner=null;t.phase='bracket';t.champion=null;advance(t);detail=`Отменён результат пары ${r+1}.${i+1}`;
    } else if(c.action==='winner') {
      if(!['bracket','completed'].includes(t.phase)) throw Error('Сначала создайте сетку');
      const match=t.rounds.flat().find(m=>m.id===c.matchId);
      if(!match || !match.a || !match.b || ![match.a,match.b].includes(c.winner)) throw Error('Пара ещё не готова или победитель не входит в пару');
      if(match.winner===c.winner) throw Error('Этот результат уже сохранён');
      const [r,i]=match.id.split('-').map(Number);
      if(match.done) {
        if(typeof c.reason!=='string'||c.reason.trim().length<3) throw Error('Для исправления победителя укажите причину');
        const next=t.rounds[r+1]?.[Math.floor(i/2)];
        if(next?.done) throw Error('Следующая пара уже решена. Исправьте её результат перед изменением предыдущей пары');
      }
      const old=match.winner;match.winner=c.winner;match.done=true;t.phase='bracket';t.champion=null;advance(t);
      detail=`Пара ${r+1}.${i+1}: победитель ${t.entries.find(p=>p.id===c.winner).name}${old?' (исправление)':''}`;
    } else if(c.action==='archive') {
      if(typeof c.reason!=='string'||c.reason.trim().length<3) throw Error('Укажите причину закрытия турнира');
      t.phase='archived';detail='Турнир закрыт, результаты сохранены';
    } else throw Error('Неизвестное действие');
  }
  t.revision++;t.updatedAt=now;
  const audit={at:now,actor:user.id,name:user.name,action:c.action,detail,reason:typeof c.reason==='string'?c.reason.trim().slice(0,200):''};
  // History is persisted separately, one immutable entry per revision.
  t.audit=[];
  return {t,audit};
}
export function summary(t) {return {id:t.id,name:t.name,phase:t.phase,createdAt:t.createdAt,updatedAt:t.updatedAt,revision:t.revision,count:t.entries.length,champion:t.entries.find(p=>p.id===t.champion)?.name??null};}
