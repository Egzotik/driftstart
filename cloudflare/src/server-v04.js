import {DurableObject} from 'cloudflare:workers';
import {RoomState} from './room.js';
import {Auth,json,fail,body,publicUser,hash} from './auth.js';
import {newTournament,mutateTournament,summary,standings} from './tournament-v10.js';

const paths=new Set(['/health','/update/manifest','/oauth/callback','/auth/start','/auth/claim','/api/me','/api/profile','/api/logout','/api/rooms','/api/members','/api/member-role','/api/member-remove','/api/online','/ws','/api/tournaments','/api/tournament','/api/history']);
const safeCode=v=>/^[A-Z2-9]{6}$/.test(v||'');
const encode=o=>btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(o))));
const decode=s=>JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(s),c=>c.charCodeAt(0))));

export default {
 async fetch(request,env){
  const url=new URL(request.url);
  if(request.method==='GET'&&url.pathname==='/health')return json({service:'DriftStart',version:'2.0.0',transport:'websocket',discordConfigured:!!env.DISCORD_CLIENT_ID&&!!env.DISCORD_CLIENT_SECRET,rooms:true});
  if(request.method==='GET'&&url.pathname==='/update/manifest'){const shaUrl=env.UPDATE_SHA256_URL||'https://github.com/Egzotik/driftstart/releases/latest/download/DriftStart.sha256';let sha256=env.UPDATE_SHA256||null;if(!sha256)try{const response=await fetch(shaUrl,{headers:{'User-Agent':'DriftStart-Updater'}});if(response.ok)sha256=(await response.text()).trim().split(/\s+/)[0];}catch{}return json({version:env.UPDATE_VERSION||'2.0.0',url:env.UPDATE_URL||'https://github.com/Egzotik/driftstart/releases/latest/download/DriftStart.exe',sha256,notes:env.UPDATE_NOTES||'Полный редизайн: компактные панели, удобный турнир, обновлённый вход и проверка сессии без ожидания каталога.'});}
  if(!paths.has(url.pathname))return json({error:'Not found'},404);
  const hub=env.ROOM.getByName('main');
  if(['/oauth/callback','/auth/start','/auth/claim','/api/me','/api/profile','/api/logout','/api/rooms','/api/members','/api/member-role','/api/member-remove','/api/online'].includes(url.pathname))return hub.fetch(request);
  const code=(request.headers.get('X-Room-Code')||'').toUpperCase();if(!safeCode(code))return json({error:'Выберите комнату'},401);
  const auth=await hub.fetch(new Request(new URL('/internal/authorize?room='+code,request.url),{headers:{Authorization:request.headers.get('Authorization')||''}}));
  if(!auth.ok)return auth;const verified=await auth.json(),headers=new Headers(request.headers);headers.set('X-Verified-Member',encode(verified.member));headers.set('X-Room-Code',code);
  return env.RACES.getByName(code).fetch(new Request(request,{headers}));
 }
};

export class DriftRoom extends DurableObject {
 constructor(ctx,env){super(ctx,env);this.ctx=ctx;this.env=env;this.auth=new Auth(ctx.storage,env);}
 async fetch(request){return this.ctx.blockConcurrencyWhile(async()=>{try{return await this.dispatch(request);}catch(e){return json({error:e.message||'Ошибка сервера'},e.status||400);}});}
 async dispatch(request){
  const u=new URL(request.url),p=u.pathname,post=request.method==='POST',s=this.ctx.storage;
  if(p==='/internal/presence'&&post&&request.headers.get('X-Internal-Presence')==='driftstart'){const b=await request.json(),key='presence:'+b.userId;if(b.online)await s.put(key,{at:Date.now()});else await s.delete(key);return json({ok:true});}
  if(p==='/auth/start'&&post)return this.auth.start(request);if(p==='/auth/claim'&&post)return this.auth.claim(request);if(p==='/oauth/callback'&&!post)return this.auth.callback(request);
  const user=await this.auth.authenticate(request);
  if(p==='/internal/authorize'){
    const code=u.searchParams.get('room'),m=await s.get(`member:${code}:${user.id}`);if(!m)fail('Вы не состоите в этой комнате',403);const profile=await s.get('profile:'+user.id);return json({member:{...publicUser(user),role:m.role,discipline:m.discipline||null,joinedAt:m.joinedAt,profile:profile||null}});
  }
  if(p==='/api/me'&&!post)return json({user:publicUser(user)});
  if(p==='/api/online'&&!post){const rows=await s.list({prefix:'presence:'}),now=Date.now(),stale=[];for(const[k,v]of rows)if(now-v.at>90000)stale.push(k);if(stale.length)await s.delete(stale);return json({online:rows.size-stale.length});}
  if(p==='/api/profile'){
   if(!post)return json({profile:(await s.get('profile:'+user.id))||null});const b=await body(request),nickname=String(b.nickname||'').trim(),staticId=String(b.staticId||'').trim(),vehicleId=String(b.vehicleId||'').trim().toLowerCase(),vehicleName=String(b.vehicleName||'').trim();
   if(nickname.length<2||nickname.length>32)fail('Ник: от 2 до 32 символов');if(!/^\d{1,10}$/.test(staticId))fail('Статик: от 1 до 10 цифр');if(!/^[a-z0-9_-]{1,40}$/.test(vehicleId)||vehicleName.length<2||vehicleName.length>64)fail('Выберите автомобиль из каталога');
   const profile={nickname,staticId,vehicleId,vehicleName,vehicleImage:b.manual?null:`https://cdn.majestic-files.net/public/master/static/img/vehicles/${vehicleId}.png`,updatedAt:Date.now()};await s.put('profile:'+user.id,profile);return json({profile});
  }
  if(p==='/api/logout'&&post){await s.delete('session:'+user.sessionHash);return json({ok:true});}
  if(p==='/api/rooms'){
   if(!post){const rows=await s.list({prefix:'user-room:'+user.id+':'}),rooms=[];for(const v of rows.values()){const room=await s.get('room:'+v.code);if(room)rooms.push({...room,role:v.role});}return json({rooms:rooms.sort((a,b)=>b.updatedAt-a.updatedAt)});}
   const b=await body(request),now=Date.now();
   if(b.action==='create'){
    if(typeof b.name!=='string'||!b.name.trim()||b.name.length>48)fail('Название группы: от 1 до 48 символов');
    const owned=await s.list({prefix:'leader-room:'+user.id+':',limit:2});if(owned.size>=1)fail('Один аккаунт может создать только одну группу');
    let code;for(let tries=0;tries<20;tries++){code=Array.from(crypto.getRandomValues(new Uint8Array(6)),x=>'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'[x%32]).join('');if(!await s.get('room:'+code))break;code=null;}if(!code)fail('Не удалось создать код. Повторите');
    const room={code,name:b.name.trim(),leaderId:user.id,leaderName:user.name,createdAt:now,updatedAt:now};
    await s.put({['room:'+code]:room,[`member:${code}:${user.id}`]:{role:'leader',joinedAt:now},[`user-room:${user.id}:${code}`]:{code,role:'leader'},[`leader-room:${user.id}:${code}`]:true});
    return json({room:{...room,role:'leader'}},201);
   }
   if(b.action==='join'){
    const code=String(b.code||'').trim().toUpperCase();if(!safeCode(code))fail('Код группы состоит из 6 символов');const room=await s.get('room:'+code);if(!room)fail('Группа с таким кодом не найдена',404);
    const key=`member:${code}:${user.id}`,current=await s.get(key);if(!current){const members=await s.list({prefix:'member:'+code+':',limit:65});if(members.size>=64)fail('В группе уже 64 участника');const m={role:'pilot',joinedAt:now};await s.put({[key]:m,[`user-room:${user.id}:${code}`]:{code,role:m.role}});}
    room.updatedAt=now;await s.put('room:'+code,room);return json({room:{...room,role:current?.role||'pilot'}});
   }
   if(b.action==='leave'){
    const code=String(b.code||'').toUpperCase(),m=await s.get(`member:${code}:${user.id}`);if(!m)fail('Вы не состоите в группе');if(m.role==='leader')fail('Лидер не может покинуть группу');
    await s.delete([`member:${code}:${user.id}`,`user-room:${user.id}:${code}`]);await this.env.RACES.getByName(code).fetch(new Request('https://internal/internal/kick',{method:'POST',headers:{'X-Internal-User':user.id}}));return json({ok:true});
   }fail('Неизвестное действие');
  }
  const code=(request.headers.get('X-Room-Code')||u.searchParams.get('room')||'').toUpperCase();if(!safeCode(code))fail('Выберите комнату');const self=await s.get(`member:${code}:${user.id}`);if(!self)fail('Вы не состоите в этой комнате',403);
   if(p==='/api/members'&&!post){const rows=await s.list({prefix:'member:'+code+':'}),members=[];for(const [k,m]of rows){const id=k.split(':').at(-1),account=await s.get('user:'+id),profile=await s.get('profile:'+id);if(account)members.push({...publicUser(account),role:m.role,discipline:m.discipline||null,joinedAt:m.joinedAt,profile:profile||null});}return json({members});}
  if(p==='/api/member-role'&&post){
   if(self.role!=='leader')fail('Только лидер группы назначает роли',403);const b=await body(request);if(!['cohost','judge','pilot'].includes(b.role)||!/^\d{17,20}$/.test(b.userId||''))fail('Некорректная роль');
    const key=`member:${code}:${b.userId}`,target=await s.get(key);if(!target)fail('Пользователь не вступил в группу');if(target.role==='leader')fail('Роль лидера изменить нельзя');if(b.role==='judge'){if(!['trajectory','angle','style'].includes(b.discipline))fail('Выберите дисциплину судьи');const members=await s.list({prefix:'member:'+code+':'});if([...members.entries()].some(([k,m])=>k!==key&&m.role==='judge'&&m.discipline===b.discipline))fail('На эту дисциплину уже назначен судья');target.discipline=b.discipline;}else delete target.discipline;target.role=b.role;
   await s.put({[key]:target,[`user-room:${b.userId}:${code}`]:{code,role:b.role},[`rolelog:${code}:${Date.now()}:${crypto.randomUUID()}`]:{at:Date.now(),actor:user.id,target:b.userId,role:b.role}});
   await this.env.RACES.getByName(code).fetch(new Request('https://internal/internal/kick',{method:'POST',headers:{'X-Internal-User':b.userId}}));return json({ok:true});
  }
  if(p==='/api/member-remove'&&post){if(self.role!=='leader')fail('Только лидер удаляет участников',403);const b=await body(request);if(b.action==='disband'){const members=await s.list({prefix:'member:'+code+':'}),keys=['room:'+code,`leader-room:${user.id}:${code}`];for(const key of members.keys()){const id=key.split(':').at(-1);keys.push(key,`user-room:${id}:${code}`);}await s.delete(keys);await this.env.RACES.getByName(code).fetch(new Request('https://internal/internal/reset',{method:'POST',headers:{'X-Internal-Reset':'yes'}}));return json({ok:true});}if(!/^\d{17,20}$/.test(b.userId||'')||b.userId===user.id)fail('Нельзя удалить лидера');const target=await s.get(`member:${code}:${b.userId}`);if(!target)fail('Участник не найден');await s.delete([`member:${code}:${b.userId}`,`user-room:${b.userId}:${code}`]);await this.env.RACES.getByName(code).fetch(new Request('https://internal/internal/kick',{method:'POST',headers:{'X-Internal-User':b.userId}}));return json({ok:true});}
  fail('Method not allowed',405);
 }
 async alarm(){await this.auth.cleanup();}
}

export class EventRoom extends DurableObject {
 constructor(ctx,env){super(ctx,env);this.ctx=ctx;this.env=env;ctx.blockConcurrencyWhile(async()=>{this.room=new RoomState(await ctx.storage.get('room'));});}
 member(request){try{return decode(request.headers.get('X-Verified-Member')||'');}catch{fail('Forbidden',403);}}
 connections(){return this.ctx.getWebSockets().filter(s=>s.readyState===WebSocket.OPEN);}
 peers(){return this.connections().map(s=>s.deserializeAttachment()).filter(p=>p.role==='pilot');}
 send(s,v){try{s.send(JSON.stringify(v));}catch{try{s.close(1011,'Reconnect');}catch{}}}
 state(s){const p=s.deserializeAttachment();return {...this.room.snapshot(this.peers(),p.id,Date.now()),onlineIds:[...new Set(this.connections().map(x=>x.deserializeAttachment().userId))],user:{id:p.userId,name:p.name,username:p.username,role:p.role}};}
 broadcast(){for(const s of this.connections())this.send(s,this.state(s));}changed(){for(const s of this.connections())this.send(s,{type:'changed'});}
 canJudge(m){return ['leader','cohost','judge'].includes(m.role);}
 async fetch(request){return this.ctx.blockConcurrencyWhile(async()=>{try{return await this.dispatch(request);}catch(e){return json({error:e.message||'Ошибка комнаты'},e.status||400);}});}
 async dispatch(request){
  const u=new URL(request.url),p=u.pathname,post=request.method==='POST',s=this.ctx.storage;
  if(p==='/internal/kick'&&post){const id=request.headers.get('X-Internal-User');for(const ws of this.connections())if(ws.deserializeAttachment().userId===id)ws.close(1000,'Role or membership updated');this.broadcast();return json({ok:true});}
  if(p==='/internal/reset'&&post&&request.headers.get('X-Internal-Reset')==='yes'){for(const ws of this.connections())ws.close(1000,'Room disbanded');await s.deleteAll();this.room=new RoomState();return json({ok:true});}
  const member=this.member(request);
   if(p==='/ws'&&!post){if(request.headers.get('Upgrade')?.toLowerCase()!=='websocket')return json({error:'WebSocket required'},426);for(const ws of this.connections())if(ws.deserializeAttachment().userId===member.id)ws.close(1000,'Reconnected');if(this.connections().length>=68)return json({error:'Комната заполнена'},429);const [client,server]=Object.values(new WebSocketPair());this.ctx.acceptWebSocket(server);server.serializeAttachment({id:crypto.randomUUID(),userId:member.id,name:member.name,username:member.username,role:member.role,discipline:member.discipline||null,nickname:member.profile?.nickname,staticId:member.profile?.staticId,vehicleName:member.profile?.vehicleName,vehicleImage:member.profile?.vehicleImage,seen:Date.now(),presenceAt:Date.now(),readyEpoch:-1,ack:-1,bucket:Date.now(),messages:0});await this.env.ROOM.getByName('main').fetch(new Request('https://internal/internal/presence',{method:'POST',headers:{'X-Internal-Presence':'driftstart','Content-Type':'application/json'},body:JSON.stringify({userId:member.id,online:true})}));this.broadcast();return new Response(null,{status:101,webSocket:client});}
  if(p==='/api/tournaments'){
   if(!post){const ix=await s.list({prefix:'event-index:'});return json({events:[...ix.values()].sort((a,b)=>b.createdAt-a.createdAt)});}if(!this.canJudge(member))fail('Нужна роль судьи',403);const b=await body(request),t=newTournament(b.name,{...member,role:'judge'});await s.put({['event:'+t.id]:{...t,audit:[]},['event-index:'+t.id]:summary(t),['audit:'+t.id+':00000000']:t.audit[0]});this.changed();return json({event:t},201);
  }
  if(p==='/api/tournament'){
   const b=post?await body(request):{},id=post?b.id:u.searchParams.get('id');if(!/^[a-f0-9-]{36}$/.test(id||''))fail('Выберите турнир');let t=await s.get('event:'+id);if(!t)fail('Турнир не найден',404);
    if(post){if(b.action==='delete'){if(!this.canJudge(member))fail('Нужна роль судьи',403);const audit=await s.list({prefix:'audit:'+id+':'});await s.delete(['event:'+id,'event-index:'+id,...audit.keys()]);this.changed();return json({ok:true});}const hub=this.env.ROOM.getByName('main'),res=await hub.fetch(new Request('https://internal/api/members?room='+request.headers.get('X-Room-Code'),{headers:{Authorization:request.headers.get('Authorization')||'','X-Room-Code':request.headers.get('X-Room-Code')}}));if(!res.ok)fail('Не удалось получить состав группы');const accounts=(await res.json()).members;const result=mutateTournament(t,b,{...member,role:this.canJudge(member)?member.role:'player'},accounts);t=result.t;await s.put({['event:'+id]:t,['event-index:'+id]:summary(t),['audit:'+id+':'+String(t.revision).padStart(8,'0')]:result.audit});this.changed();}
   const audit=await s.list({prefix:'audit:'+id+':',reverse:true,limit:200});return json({event:{...t,standings:standings(t),audit:[...audit.values()]}});
  }
  if(p==='/api/history'&&!post){const id=u.searchParams.get('id'),before=u.searchParams.get('before');if(!/^[a-f0-9-]{36}$/.test(id||''))fail('Турнир не найден');const prefix='audit:'+id+':',rows=await s.list({prefix,reverse:true,limit:200,...(before?{end:prefix+before}:{})});return json({audit:[...rows.values()],next:rows.size===200?[...rows.keys()].at(-1).slice(prefix.length):null});}
  fail('Method not allowed',405);
 }
 async webSocketMessage(socket,data){return this.ctx.blockConcurrencyWhile(async()=>{const p=socket.deserializeAttachment(),now=Date.now();if(typeof data!=='string'||data.length>4096){socket.close(1009,'Message too large');return;}if(now-p.bucket>1000){p.bucket=now;p.messages=0;}if(++p.messages>16){socket.close(1008,'Rate exceeded');return;}p.seen=now;let m;try{m=JSON.parse(data);}catch{socket.close(1008,'Invalid JSON');return;}try{if(m.type==='ping'){if(now-(p.presenceAt||0)>30000){p.presenceAt=now;await this.env.ROOM.getByName('main').fetch(new Request('https://internal/internal/presence',{method:'POST',headers:{'X-Internal-Presence':'driftstart','Content-Type':'application/json'},body:JSON.stringify({userId:p.userId,online:true})}));}socket.serializeAttachment(p);this.send(socket,{type:'pong',nonce:m.nonce,serverMs:now,state:this.state(socket)});return;}if(m.type==='ack'){p.ack=m.version===this.room.version?m.version:-1;socket.serializeAttachment(p);this.broadcast();}else if(m.type==='ready'){if(p.role!=='pilot')throw Error('Готовность отмечают пилоты');if(this.room.active(now))throw Error('Дождитесь завершения отсчёта');p.readyEpoch=m.ready&&m.epoch===this.room.readyEpoch?this.room.readyEpoch:-1;socket.serializeAttachment(p);this.broadcast();}else if(m.type==='control'){if(!this.canJudge(p))throw Error('Команда доступна судьям');this.room.control(m,this.peers(),now);if(m.action==='prune')for(const ws of this.connections())if(now-ws.deserializeAttachment().seen>3000)ws.close(1000,'Inactive');await this.ctx.storage.put('room',{...this.room});this.broadcast();this.send(socket,{type:'result',requestId:m.requestId,action:m.action,ok:true});}else throw Error('Неизвестная команда');}catch(e){this.send(socket,{type:'error',error:e.message});}});}
 async webSocketClose(socket,code){const userId=socket.deserializeAttachment()?.userId;try{socket.close(code===1006?1000:code);}catch{}if(userId&&!this.connections().some(x=>x!==socket&&x.deserializeAttachment().userId===userId))await this.env.ROOM.getByName('main').fetch(new Request('https://internal/internal/presence',{method:'POST',headers:{'X-Internal-Presence':'driftstart','Content-Type':'application/json'},body:JSON.stringify({userId,online:false})}));this.broadcast();}async webSocketError(socket){try{socket.close(1011,'Connection error');}catch{}this.broadcast();}
}
