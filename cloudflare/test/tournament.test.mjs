import {test} from 'node:test';
import assert from 'node:assert/strict';
import {newTournament,mutateTournament,standings} from '../src/tournament-v10.js';
const owner={id:'100000000000000001',name:'Owner',role:'judge',discipline:'trajectory'},pilot={id:'100000000000000002',name:'Pilot',role:'player'};
const users=[owner,pilot,...Array.from({length:6},(_,i)=>({id:String(100000000000000010n+BigInt(i)),name:'Pilot '+i,role:'player'}))];
const edit=(t,c,user=owner)=>mutateTournament(t,{...c,revision:t.revision},user,users).t;
test('permissions, stale revisions, no partial mutation on rejection',()=>{
  assert.throws(()=>newTournament('x',pilot));let t=newTournament('Cup',owner);t=edit(t,{action:'join',userId:pilot.id});
  const before=JSON.stringify(t);assert.throws(()=>edit(t,{action:'score',userId:pilot.id,attempt:0,score:99},pilot));
  for(const score of [-1,101,NaN,'99',99.123])assert.throws(()=>edit(t,{action:'score',userId:pilot.id,attempt:0,score}));
  assert.throws(()=>mutateTournament(t,{action:'join',revision:0},pilot,users));assert.equal(JSON.stringify(t),before);
});
test('qualification rankings, audit corrections, deterministic complete ties',()=>{
  let t=newTournament('Cup',owner);for(const u of users.slice(0,3))t=edit(t,{action:'join',userId:u.id});
  for(const [i,u]of users.slice(0,3).entries()){t=edit(t,{action:'score',userId:u.id,attempt:0,score:30});t=edit(t,{action:'score',userId:u.id,attempt:1,score:i?25:20});}
  assert.deepEqual(standings(t).map(p=>p.id),[users[1].id,users[2].id,users[0].id]);
  assert.throws(()=>edit(t,{action:'score',userId:owner.id,attempt:0,score:98}));
  const {t:changed,audit}=mutateTournament(t,{revision:t.revision,action:'score',userId:owner.id,attempt:0,score:40,reason:'Input correction'},owner,users,12345);
  assert.equal(audit.at,12345);assert.equal(audit.actor,owner.id);assert.match(audit.detail,/30 → 40/);assert.equal(standings(changed)[0].id,owner.id);
});
test('seeding, byes, progression, championship and immutable archive',()=>{
  let t=newTournament('Cup',owner);for(const [i,u]of users.slice(0,5).entries()){t=edit(t,{action:'join',userId:u.id});for(let attempt=0;attempt<2;attempt++)t=edit(t,{action:'score',userId:u.id,attempt,score:40-i});}
  t=edit(t,{action:'bracket'});assert.equal(t.rounds[0].filter(m=>m.bye).length,3);
  assert.equal(t.rounds[0][0].a,owner.id);assert.equal(t.rounds[0][0].b,null);assert.equal(t.rounds[0][1].a,users[3].id);assert.equal(t.rounds[0][1].b,users[4].id);
  assert.throws(()=>edit(t,{action:'score',userId:owner.id,attempt:0,score:1,reason:'bad'}));
  assert.throws(()=>edit(t,{action:'winner',matchId:'2-0',winner:owner.id}));
  for(let guard=0;guard<20;guard++){const m=t.rounds.flat().find(x=>!x.done&&x.a&&x.b);if(!m)break;t=edit(t,{action:'winner',matchId:m.id,winner:m.a});}if(t.thirdPlace&&!t.thirdPlace.done)t=edit(t,{action:'winner',matchId:'third',winner:t.thirdPlace.a});
  assert.equal(t.phase,'completed');assert.equal(t.champion,owner.id);assert.equal(t.placements.first,owner.id);assert.ok(t.placements.third);assert.equal(t.placements.qualificationLeader,owner.id);
  t=edit(t,{action:'archive',reason:'Tournament finished'});assert.throws(()=>edit(t,{action:'join'}));
});
test('only drivers with more than one point enter the automatic bracket',()=>{
  let t=newTournament('Cup',owner);for(const u of users.slice(0,2))t=edit(t,{action:'join',userId:u.id});
  assert.throws(()=>edit(t,{action:'bracket'}));for(const u of users.slice(0,2))for(let attempt=0;attempt<2;attempt++)t=edit(t,{action:'score',userId:u.id,attempt,score:0});
  assert.throws(()=>edit(t,{action:'bracket'}));for(const u of users.slice(0,2))t=edit(t,{action:'score',userId:u.id,attempt:0,score:2,reason:'qualifying result'});t=edit(t,{action:'bracket'});assert.equal(t.rounds[0][0].a,owner.id);
});
test('judge can register a driver without the app',()=>{let t=newTournament('Offline Cup',owner);t=edit(t,{action:'manualJoin',name:'Offline Driver',staticId:'7711',vehicleName:'Karin Futo'});assert.equal(t.entries.length,1);assert.equal(t.entries[0].staticId,'7711');assert.equal(t.entries[0].manual,true);});
test('judge removal deletes the driver from qualification standings',()=>{let t=newTournament('Cup',owner);t=edit(t,{action:'join',userId:pilot.id});t=edit(t,{action:'removeEntry',userId:pilot.id});assert.equal(t.entries.length,0);assert.equal(standings(t).length,0);});
test('three discipline judges create the attempt total',()=>{let t=newTournament('Judged Cup',owner);t=edit(t,{action:'join',userId:pilot.id});t=edit(t,{action:'score',userId:pilot.id,attempt:0,score:40});const angle={...users[2],role:'judge',discipline:'angle'},style={...users[3],role:'judge',discipline:'style'};t=mutateTournament(t,{action:'score',revision:t.revision,userId:pilot.id,attempt:0,score:35},angle,users).t;t=mutateTournament(t,{action:'score',revision:t.revision,userId:pilot.id,attempt:0,score:20},style,users).t;assert.equal(t.entries[0].scores[0],95);assert.deepEqual(t.entries[0].disciplineScores[0],{trajectory:40,angle:35,style:20});});
test('group leader can score a selected discipline',()=>{const leader={...owner,role:'leader',discipline:undefined};let t=newTournament('Leader Cup',leader);t=mutateTournament(t,{action:'join',revision:t.revision,userId:pilot.id},leader,users).t;t=mutateTournament(t,{action:'score',revision:t.revision,userId:pilot.id,attempt:0,discipline:'style',score:25},leader,users).t;assert.equal(t.entries[0].scores[0],25);assert.equal(t.entries[0].disciplineScores[0].style,25);});
test('attempt modal saves three fields for leader and only assigned field for judge',()=>{const leader={...owner,role:'leader',discipline:undefined};let t=newTournament('Modal Cup',leader);t=mutateTournament(t,{action:'join',revision:t.revision,userId:pilot.id},leader,users).t;t=mutateTournament(t,{action:'scoreAttempt',revision:t.revision,userId:pilot.id,attempt:0,scores:{trajectory:40,angle:35,style:25}},leader,users).t;assert.equal(t.entries[0].scores[0],100);const angle={...users[2],role:'judge',discipline:'angle'};t=mutateTournament(t,{action:'scoreAttempt',revision:t.revision,userId:pilot.id,attempt:0,scores:{trajectory:0,angle:31,style:0}},angle,users).t;assert.equal(t.entries[0].scores[0],96);assert.deepEqual(t.entries[0].disciplineScores[0],{trajectory:40,angle:31,style:25});});
test('tandem scores persist for leader and follower before roles swap',()=>{let t=newTournament('Tandem Cup',owner);for(const u of users.slice(0,2)){t=edit(t,{action:'join',userId:u.id});t=edit(t,{action:'score',userId:u.id,attempt:0,score:40});}t=edit(t,{action:'bracket'});const m=t.rounds[0][0],a=m.a,b=m.b,angle={...users[2],role:'judge',discipline:'angle'};t=edit(t,{action:'tandemScore',matchId:m.id,userId:a,run:0,score:40});t=mutateTournament(t,{action:'tandemScore',revision:t.revision,matchId:m.id,userId:a,run:0,score:35},angle,users).t;t=edit(t,{action:'tandemScore',matchId:m.id,userId:a,run:1,score:38});t=edit(t,{action:'tandemScore',matchId:m.id,userId:b,run:0,score:37});assert.equal(t.rounds[0][0].tandemScores[0][a],75);assert.equal(t.rounds[0][0].tandemScores[1][a],38);assert.equal(t.rounds[0][0].tandemScores[0][b],37);assert.deepEqual(t.rounds[0][0].tandemDisciplineScores[0][a],{trajectory:40,angle:35});});
test('judges can drag a first-round seed into a bye slot before results',()=>{let t=newTournament('Seed Cup',owner);for(const [i,u]of users.slice(0,5).entries()){t=edit(t,{action:'join',userId:u.id});t=edit(t,{action:'score',userId:u.id,attempt:0,score:40-i});}t=edit(t,{action:'bracket'});const moved=t.rounds[0][0].a,target=t.rounds[0][1].a;t=edit(t,{action:'moveSeed',userId:moved,targetMatch:'0-1',targetSlot:'a'});assert.equal(t.rounds[0][1].a,moved);assert.equal(t.rounds[0][0].a,target);assert.ok(t.rounds[0].filter(m=>m.bye).every(m=>m.done));});
test('correcting earlier winners requires undoing downstream results; empty slots never retain old entrants',()=>{
  let t=newTournament('Cup',owner);for(const u of users.slice(0,4)){t=edit(t,{action:'join',userId:u.id});for(let attempt=0;attempt<2;attempt++)t=edit(t,{action:'score',userId:u.id,attempt,score:40});}
  t=edit(t,{action:'bracket'});for(const m of t.rounds[0])t=edit(t,{action:'winner',matchId:m.id,winner:m.a});t=edit(t,{action:'winner',matchId:'1-0',winner:t.rounds[1][0].a});
  assert.throws(()=>edit(t,{action:'clearWinner',matchId:'0-0',reason:'Correction'}));
  t=edit(t,{action:'clearWinner',matchId:'1-0',reason:'Correction'});assert.equal(t.champion,null);assert.equal(t.phase,'bracket');
  t=edit(t,{action:'clearWinner',matchId:'0-0',reason:'Correction'});assert.equal(t.rounds[1][0].a,null);
  t=edit(t,{action:'winner',matchId:'0-0',winner:t.rounds[0][0].b});assert.equal(t.rounds[1][0].a,t.rounds[0][0].b);
});
