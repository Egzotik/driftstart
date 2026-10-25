import {DurableObject} from 'cloudflare:workers';
import {RoomState} from './room.js';
import {Auth,json,fail,matches,body,publicUser} from './auth.js';
import {newTournament,mutateTournament,summary,standings} from './tournament.js';
export default {
  async fetch(request,env){
    const url=new URL(request.url);
    if(request.method==='GET'&&url.pathname==='/health')return json({service:'DriftStart',version:'0.3.0',transport:'websocket',discordConfigured:!!env.DISCORD_CLIENT_ID&&!!env.DISCORD_CLIENT_SECRET});
    if(!['GET','POST'].includes(request.method))return json({error:'Method not allowed'},405);
    if(!['/ws','/oauth/callback','/auth/start','/auth/claim','/api/me','/api/logout','/api/bootstrap','/api/accounts','/api/role','/api/tournaments','/api/tournament','/api/history'].includes(url.pathname))return json({error:'Not found'},404);
    if(url.pathname!=='/oauth/callback'&&!await matches(request.headers.get('X-Room-Code')||'',env.DRIFT_ROOM_CODE))return json({error:'Неверный код комнаты'},401);
    return env.ROOM.getByName('main').fetch(request);
  }
};
export class DriftRoom extends DurableObject {
  constructor(ctx,env){super(ctx,env);this.ctx=ctx;this.env=env;this.auth=new Auth(ctx.storage,env);ctx.blockConcurrencyWhile(async()=>{this.room=new RoomState(await ctx.storage.get('room'));});}
  connections(){return this.ctx.getWebSockets().filter(s=>s.readyState===WebSocket.OPEN);}
  peers(){return this.connections().map(s=>s.deserializeAttachment()).filter(p=>p.role==='player');}
  send(socket,value){try{socket.send(JSON.stringify(value));}catch{try{socket.close(1011,'Reconnect');}catch{}}}
  state(socket){const p=socket.deserializeAttachment();return {...this.room.snapshot(this.peers(),p.id,Date.now()),user:{id:p.userId,name:p.name,role:p.role}};}
  broadcast(){for(const s of this.connections())this.send(s,this.state(s));}
  changed(){for(const s of this.connections())this.send(s,{type:'changed'});}
  async fetch(request){return this.ctx.blockConcurrencyWhile(async()=>{try{return await this.dispatch(request);}catch(e){return json({error:e.message||'Не удалось выполнить запрос'},e.status||400);}});}
  async dispatch(request){
    const url=new URL(request.url),path=url.pathname,post=request.method==='POST',store=this.ctx.storage;
    if(path==='/auth/start'&&post)return this.auth.start(request);
    if(path==='/auth/claim'&&post){const response=await this.auth.claim(request);if(response.status===200)this.changed();return response;}
    if(path==='/oauth/callback'&&!post)return this.auth.callback(request);
    const user=await this.auth.authenticate(request);
    if(path==='/ws'&&!post)return this.connect(request,user);
    if(path==='/api/me'&&!post)return json({user:publicUser(user),ownerBound:!!await store.get('ownerId')});
    if(path==='/api/logout'&&post){await store.delete('session:'+user.sessionHash);this.closeUser(user.id);return json({ok:true});}
    if(path==='/api/bootstrap'&&post){
      if(await store.get('ownerId'))fail('Владелец уже назначен',409);
      const rateKey='rate:bootstrap:'+user.id,rate=await store.get(rateKey),now=Date.now();
      if(rate?.expires>now&&rate.count>=5)fail('Подождите минуту перед следующей попыткой',429);
      await store.put(rateKey,{count:rate?.expires>now?rate.count+1:1,expires:now+60000});
      const b=await body(request);if(!await matches(b.key||'',this.env.DRIFT_JUDGE_KEY))fail('Неверный ключ владельца',403);
      user.role='owner';await store.put({'ownerId':user.id,['user:'+user.id]:user});
      await store.put('rolelog:'+Date.now()+':'+crypto.randomUUID(),{at:Date.now(),actor:user.id,target:user.id,role:'owner'});
      this.closeUser(user.id);this.changed();return json({user:publicUser(user),ownerBound:true});
    }
    if(path==='/api/accounts'&&!post){const users=await store.list({prefix:'user:'});return json({users:[...users.values()].map(publicUser)});}
    if(path==='/api/role'&&post){
      if(user.role!=='owner')fail('Только владелец назначает судей',403);
      const b=await body(request);if(!['judge','player'].includes(b.role)||!/^\d{17,20}$/.test(b.userId||''))fail('Некорректная роль или Discord ID');
      const target=await store.get('user:'+b.userId);if(!target)fail('Этот аккаунт ещё не входил в комнату');if(target.role==='owner')fail('Роль владельца нельзя снять',403);
      target.role=b.role;await store.put('user:'+target.id,target);await store.put('rolelog:'+Date.now()+':'+crypto.randomUUID(),{at:Date.now(),actor:user.id,target:target.id,role:b.role});
      this.closeUser(target.id);this.changed();return json({ok:true});
    }
    if(path==='/api/tournaments'){
      if(!post){const index=await store.list({prefix:'event-index:'});return json({events:[...index.values()].sort((a,b)=>b.createdAt-a.createdAt)});}
      const b=await body(request),t=newTournament(b.name,user);
      await store.put({['event:'+t.id]:{...t,audit:[]},['event-index:'+t.id]:summary(t),['audit:'+t.id+':00000000']:t.audit[0]});this.changed();return json({event:t},201);
    }
    if(path==='/api/tournament'){
      const b=post?await body(request):{},id=post?b.id:url.searchParams.get('id');if(!/^[a-f0-9-]{36}$/.test(id||''))fail('Выберите турнир');
      let t=await store.get('event:'+id);if(!t)fail('Турнир не найден',404);
      if(post){const accounts=[...(await store.list({prefix:'user:'})).values()];const result=mutateTournament(t,b,user,accounts);t=result.t;
        await store.put({['event:'+id]:t,['event-index:'+id]:summary(t),['audit:'+id+':'+String(t.revision).padStart(8,'0')]:result.audit});this.changed();}
      const audit=await store.list({prefix:'audit:'+id+':',reverse:true,limit:200});return json({event:{...t,standings:standings(t),audit:[...audit.values()]},historyLimit:200});
    }
    if(path==='/api/history'&&!post){
      const id=url.searchParams.get('id'),before=url.searchParams.get('before');if(!/^[a-f0-9-]{36}$/.test(id||'')||(before&&!/^\d{8}$/.test(before)))fail('Некорректный запрос истории');
      if(!await store.get('event:'+id))fail('Турнир не найден',404);
      const prefix='audit:'+id+':',rows=await store.list({prefix,reverse:true,limit:200,...(before?{end:prefix+before}:{})});
      return json({audit:[...rows.values()],next:rows.size===200?[...rows.keys()].at(-1).slice(prefix.length):null});
    }
    fail('Method not allowed',405);
  }
  closeUser(id){for(const s of this.connections())if(s.deserializeAttachment().userId===id)s.close(1000,'Account updated; reconnect');this.broadcast();}
  connect(request,user){
    if(request.headers.get('Upgrade')?.toLowerCase()!=='websocket')return json({error:'WebSocket required'},426);
    this.closeUser(user.id);if(this.connections().length>=68||(user.role==='player'&&this.peers().length>=64))return json({error:'Комната заполнена'},429);
    const [client,server]=Object.values(new WebSocketPair());this.ctx.acceptWebSocket(server);
    server.serializeAttachment({id:crypto.randomUUID(),userId:user.id,username:user.username,sessionHash:user.sessionHash,name:user.name,role:user.role,seen:Date.now(),readyEpoch:-1,ack:-1,bucket:Date.now(),messages:0});this.broadcast();return new Response(null,{status:101,webSocket:client});
  }
  async webSocketMessage(socket,data){return this.ctx.blockConcurrencyWhile(async()=>{
    const p=socket.deserializeAttachment(),now=Date.now();
    if(typeof data!=='string'||data.length>4096){socket.close(1009,'Message too large');return;}
    if(now-p.bucket>1000){p.bucket=now;p.messages=0;}if(++p.messages>16){socket.close(1008,'Message rate exceeded');return;}p.seen=now;
    let m;try{m=JSON.parse(data);}catch{socket.close(1008,'Invalid JSON');return;}if(!m||typeof m!=='object'){socket.close(1008,'Invalid message');return;}
    try{
      const user=await this.auth.byHash(p.sessionHash);p.role=user.role;p.name=user.name;socket.serializeAttachment(p);
      if(m.type==='ping'){this.send(socket,{type:'pong',nonce:m.nonce,serverMs:now,state:this.state(socket)});return;}
      if(m.type==='ack'){p.ack=m.version===this.room.version?m.version:-1;socket.serializeAttachment(p);this.broadcast();}
      else if(m.type==='ready'){
        if(p.role!=='player')throw Error('Готовность отмечают пилоты');if(this.room.active(now))throw Error('Дождитесь завершения отсчёта');p.readyEpoch=m.ready&&m.epoch===this.room.readyEpoch?this.room.readyEpoch:-1;socket.serializeAttachment(p);this.broadcast();
      }else if(m.type==='control'){
        if(!['judge','owner'].includes(user.role))fail('Команда доступна только судье',403);this.room.control(m,this.peers(),now);
        if(m.action==='prune')for(const s of this.connections())if(now-s.deserializeAttachment().seen>3000)s.close(1000,'Inactive participant removed');
        await this.ctx.storage.put('room',{...this.room});this.broadcast();this.send(socket,{type:'result',requestId:m.requestId,action:m.action,ok:true});
      }else throw Error('Неизвестный тип сообщения');
    }catch(e){this.send(socket,{type:'error',requestId:m.requestId,error:e.message});if(e.status===401)socket.close(1008,'Session expired');}
  });}
  async alarm(){await this.auth.cleanup();}
  async webSocketClose(socket,code){try{socket.close(code===1006?1000:code);}catch{}this.broadcast();}
  async webSocketError(socket){try{socket.close(1011,'Connection error');}catch{}this.broadcast();}
}
