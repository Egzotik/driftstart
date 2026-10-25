import WebSocket from 'ws';
import assert from 'node:assert/strict';
import {setTimeout as sleep} from 'node:timers/promises';
const base=process.env.DRIFT_SERVER_URL??'http://127.0.0.1:8788';
const room=process.env.DRIFT_ROOM_CODE??'drift-local-test';
const key=process.env.DRIFT_JUDGE_KEY??'local-judge-key-for-tests-2026';
const checks=[], clients=[];
async function until(fn,label,timeout=10000){const end=Date.now()+timeout;while(!fn()){if(Date.now()>end)throw Error(label+' timeout');await sleep(20);}checks.push(label);}
async function client(name,judge=false,roomCode=room,judgeKey=key){
 const result={state:null,errors:[],lastPong:null};
 const socket=new WebSocket(base.replace(/^http/,'ws')+'/ws',{headers:{'X-Room-Code':roomCode,'X-Client-Role':judge?'judge':'player','X-Client-Name':Buffer.from(name).toString('base64'),'X-Judge-Key':judgeKey}});
 result.ws=socket;clients.push(result);result.send=m=>socket.send(JSON.stringify(m));
 socket.on('message',data=>{const m=JSON.parse(data);if(m.type==='state'){result.state=m;if(result.ack!==m.version){result.ack=m.version;result.send({type:'ack',version:m.version});}}else if(m.type==='pong'){result.lastPong=m;result.state=m.state;}else if(m.type==='error')result.errors.push(m.error);});
 await new Promise((resolve,reject)=>{socket.once('open',resolve);socket.once('error',reject);});
 result.ping=setInterval(()=>{if(socket.readyState===WebSocket.OPEN)result.send({type:'ping',nonce:Date.now()});},500);
 return result;
}
try{
 assert.equal((await fetch(base+'/health')).status,200);checks.push('HTTPS health endpoint');
 await assert.rejects(()=>client('unauthorized',false,'wrong-code'));checks.push('Room authentication rejects wrong code');
 await assert.rejects(()=>client('fake judge',true,room,'wrong-key'));checks.push('Judge authentication rejects wrong key');
 const judge=await client('Judge',true),a=await client('Пилот A'),b=await client('Driver B');
 await until(()=>judge.state?.peers.length===2,'Both pilots appear in judge roster');
 judge.send({type:'control',action:'start'});await until(()=>judge.errors.length>0,'Unready start rejected');
 const errA=a.errors.length;a.send({type:'control',action:'cancel'});await until(()=>a.errors.length>errA,'Pilot cannot control race');
 for(const p of [a,b])p.send({type:'ready',ready:true,epoch:p.state.readyEpoch});
 await until(()=>judge.state.peers.every(p=>p.ready&&p.received),'Readiness and delivery acknowledged');
 judge.send({type:'control',action:'start',label:'Transport test',delayMs:6000});
 await until(()=>a.state.startAt&&b.state.startAt===a.state.startAt,'Same start timestamp pushed to both clients');
 await until(()=>judge.state.peers.every(p=>p.received),'Start command delivery acknowledged');
 const late=await client('Late joiner');await until(()=>late.state!==null,'Late client receives state');assert.equal(late.state.seated,false);checks.push('Late joiner does not receive a green start');
 await until(()=>a.lastPong&&b.lastPong,'Application heartbeat contains server time');
 judge.send({type:'control',action:'cancel'});await until(()=>a.state.startAt===null&&b.state.startAt===null,'Cancellation reaches all clients');
 assert.ok(a.state.peers.every(p=>!p.ready));checks.push('Cancellation invalidates readiness');
 late.ws.close();await until(()=>judge.state.peers.length===2,'Disconnected pilot removed from roster');
 console.log(JSON.stringify({result:'PASS',checks:checks.length,details:checks},null,2));
}finally{for(const c of clients){clearInterval(c.ping);c.ws.terminate();}}
