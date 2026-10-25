import {Miniflare,convertV4MiniflareOptions} from 'miniflare';
import assert from 'node:assert/strict';
import {randomBytes,createHash} from 'node:crypto';
import {mkdtemp} from 'node:fs/promises';
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {readFile} from 'node:fs/promises';
const dir=await mkdtemp(path.resolve('../integration-state-'));
const opts={modules:['server.js','room.js','auth.js','tournament.js'].map(file=>({type:'ESModule',path:path.resolve('src/'+file)})),compatibilityDate:'2026-10-01',durableObjects:{ROOM:{className:'DriftRoom',useSQLite:true}},durableObjectsPersist:dir,bindings:{DRIFT_ROOM_CODE:'test-room-2026',DRIFT_JUDGE_KEY:'test-owner-bootstrap-2026',DISCORD_CLIENT_ID:'1563601932835360898',DISCORD_CLIENT_SECRET:'test-secret-only'},
  outboundService:async req=>{const url=new URL(req.url);if(url.pathname==='/api/oauth2/token'){const f=await req.formData();assert.equal(f.get('client_secret'),'test-secret-only');if(f.get('code')==='invalid-secret-test')return Response.json({error:'invalid_client',error_description:'Do not expose raw upstream descriptions'},{status:401});return Response.json({access_token:f.get('code')});}if(url.pathname==='/api/v10/users/@me'){const id=req.headers.get('Authorization').slice(7);return Response.json({id,username:'pilot'+id.slice(-2),global_name:'Driver '+id.slice(-2)});}throw Error('Unexpected outbound request: '+url.pathname);}};
const options=convertV4MiniflareOptions(opts);options.resourcePersistencePath=dir;
let mf=new Miniflare(options),checks=0;
const ok=(value,message)=>{assert.ok(value,message);console.log('PASS '+message);checks++;};
async function call(route,token='',value,extra={}){const res=await mf.dispatchFetch('https://test.local'+route,{method:value===undefined?'GET':'POST',headers:{'X-Room-Code':'test-room-2026',...(token?{Authorization:'Bearer '+token}:{}),...extra},...(value===undefined?{}:{body:JSON.stringify(value)})});return {status:res.status,data:await res.json()};}
async function login(id){const verifier=randomBytes(32).toString('hex'),challenge=createHash('sha256').update(verifier).digest('hex');const start=await call('/auth/start','',{challenge});assert.equal(start.status,200);const state=start.data.id;
  assert.equal((await call('/auth/claim','',{id:state,verifier:'0'.repeat(64)})).status,403);
  const cb=await mf.dispatchFetch('https://test.local/oauth/callback?state='+state+'&code='+id);assert.equal(cb.status,200,await cb.text());
  const claim=await call('/auth/claim','',{id:state,verifier});assert.equal(claim.status,200);assert.equal((await call('/auth/claim','',{id:state,verifier})).status,410);return claim.data.token;}
async function ws(token){const res=await mf.dispatchFetch('https://test.local/ws',{headers:{'X-Room-Code':'test-room-2026',Authorization:'Bearer '+token,Upgrade:'websocket','X-Client-Role':'owner','X-Client-Name':'U3Bvb2ZlZA=='}});assert.equal(res.status,101);const s=res.webSocket;s.accept();const messages=[];s.addEventListener('message',e=>messages.push(JSON.parse(e.data)));return {s,messages};}
const pause=ms=>new Promise(r=>setTimeout(r,ms));
try{
  ok((await call('/api/me')).status===401,'unauthenticated APIs denied');
  ok((await call('/api/me','','',{'X-Room-Code':'wrong'})).status===401,'incorrect room denied');
  const badVerifier=randomBytes(32).toString('hex'),badStart=await call('/auth/start','',{challenge:createHash('sha256').update(badVerifier).digest('hex')});
  const badCallback=await mf.dispatchFetch('https://test.local/oauth/callback?state='+badStart.data.id+'&code=invalid-secret-test');const badError=await badCallback.json();ok(badCallback.status===502&&badError.error.includes('Client Secret')&&!badError.error.includes('upstream'),'OAuth errors are specific and sanitized');
  const badClaim=await call('/auth/claim','',{id:badStart.data.id,verifier:badVerifier});ok(badClaim.status===400&&badClaim.data.error.includes('Client Secret'),'desktop receives actionable OAuth failure');
  const owner=await login('100000000000000001'),judge=await login('100000000000000002'),pilot=await login('100000000000000003');ok(true,'OAuth state, proof binding and single-use claim');
  ok((await call('/api/me',owner)).data.user.role==='player','first login does not grant owner');
  ok((await call('/api/bootstrap',owner,{key:'wrong'})).status===403,'bootstrap requires private organizer key');
  ok((await call('/api/bootstrap',owner,{key:'test-owner-bootstrap-2026'})).data.user.role==='owner','owner binding persists by Discord ID');
  ok((await call('/api/bootstrap',judge,{key:'test-owner-bootstrap-2026'})).status===409,'owner cannot be claimed twice');
  ok((await call('/api/role',pilot,{userId:'100000000000000003',role:'judge'})).status===403,'player cannot self-promote');
  await call('/api/role',owner,{userId:'100000000000000002',role:'judge'});ok((await call('/api/me',judge)).data.user.role==='judge','owner can assign judge');
  const j=await ws(judge),p=await ws(pilot);p.s.send(JSON.stringify({type:'ping',nonce:1}));await pause(100);
  const state=p.messages.find(m=>m.type==='pong').state;ok(state.user.role==='player'&&state.user.name!=='Spoofed','WebSocket identity and role ignore spoofed headers');
  p.s.send(JSON.stringify({type:'control',action:'cancel'}));await pause(100);ok(p.messages.some(m=>m.type==='error'),'player cannot send judge commands');
  p.s.send(JSON.stringify({type:'ack',version:state.version}));p.s.send(JSON.stringify({type:'ready',ready:true,epoch:state.readyEpoch}));await pause(100);j.s.send(JSON.stringify({type:'control',action:'start',delayMs:6000}));await pause(100);ok(j.messages.some(m=>m.type==='result'&&m.action==='start'),'authenticated judge starts ready pilot');
  await call('/api/role',owner,{userId:'100000000000000002',role:'player'});await pause(100);ok((await call('/api/me',judge)).data.user.role==='player','judge revocation is immediately authoritative');
  ok((await call('/api/tournaments',judge,{name:'Forbidden'})).status===400,'revoked judge cannot create tournaments');
  await call('/api/role',owner,{userId:'100000000000000002',role:'judge'});
  let response=await call('/api/tournaments',judge,{name:'Integration Cup'}),t=response.data.event;assert.equal(response.status,201);
  const command=async c=>{const res=await call('/api/tournament',judge,{id:t.id,revision:t.revision,...c});assert.equal(res.status,200,JSON.stringify(res.data));t=res.data.event;};
  for(const userId of ['100000000000000001','100000000000000003'])await command({action:'join',userId});
  ok((await call('/api/tournament',pilot,{id:t.id,revision:t.revision,action:'score',userId:t.entries[0].id,attempt:0,score:100})).status===400,'pilot cannot edit scores');
  for(const [i,p] of t.entries.entries())for(let attempt=0;attempt<2;attempt++)await command({action:'score',userId:p.id,attempt,score:98-i});
  const stale=await call('/api/tournament',judge,{id:t.id,revision:0,action:'bracket',size:2});ok(stale.status===400,'stale concurrent writes rejected');
  await command({action:'bracket',size:2});await command({action:'winner',matchId:'0-0',winner:t.entries[0].id});ok(t.phase==='completed'&&t.audit.length===9,'qualification, bracket, winner and audited history');
  p.s.close();j.s.close();await mf.dispose();mf=new Miniflare(options);
  const restored=await call('/api/tournament?id='+t.id,owner);ok(restored.data.event.champion===t.champion&&restored.data.event.audit.length===9,'results, history and session survive Worker restart');
  const older=await call('/api/history?id='+t.id+'&before=00000004',owner);ok(older.data.audit.length===4,'history pagination returns only earlier revisions');
  if(process.env.DRIFT_EXE){
    const pilotB=await login('100000000000000004');const control=await ws(owner);control.s.send(JSON.stringify({type:'control',action:'cancel'}));await pause(100);control.s.close();await pause(100);
    const report=path.resolve('../client-v03-report.json');await promisify(execFile)(process.env.DRIFT_EXE,['--verify',report],{timeout:45000,windowsHide:true,env:{...process.env,DRIFT_SERVER_URL:String(await mf.ready).replace(/\/$/,''),DRIFT_ROOM_CODE:'test-room-2026',DRIFT_TEST_JUDGE_TOKEN:owner,DRIFT_TEST_A_TOKEN:pilot,DRIFT_TEST_B_TOKEN:pilotB,DOTNET_BUNDLE_EXTRACT_BASE_DIR:path.resolve('../extract')}});
    const result=JSON.parse(await readFile(report,'utf8'));ok(result.result==='PASS','Windows EXE: '+result.checks+' checks of authenticated synchronization and DPAPI');if(result.result!=='PASS')console.log(result);
  }
  await call('/api/logout',pilot,{});ok((await call('/api/me',pilot)).status===401,'logout revokes server session');
  console.log(JSON.stringify({passed:checks,persistenceDirectory:dir}));
}finally{await mf.dispose();}

