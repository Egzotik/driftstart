export const json=(body,status=200)=>Response.json(body,{status,headers:{'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'}});
export function fail(message,status=400){const e=Error(message);e.status=status;throw e;}
export const random=()=>Array.from(crypto.getRandomValues(new Uint8Array(32)),v=>v.toString(16).padStart(2,'0')).join('');
export async function hash(value){return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(value))),v=>v.toString(16).padStart(2,'0')).join('');}
export async function matches(a,b){if(typeof a!=='string'||!b||a.length>512)return false;const x=await hash(a),y=await hash(b);let d=0;for(let i=0;i<x.length;i++)d|=x.charCodeAt(i)^y.charCodeAt(i);return d===0;}
export async function body(request){if(Number(request.headers.get('Content-Length'))>8192)fail('Слишком большой запрос',413);const text=await request.text();if(text.length>8192)fail('Слишком большой запрос',413);try{const v=JSON.parse(text);if(!v||typeof v!=='object'||Array.isArray(v))throw Error();return v;}catch{fail('Неверный JSON');}}
export function publicUser(u){return {id:u.id,name:u.name,username:u.username,role:u.role};}
export class Auth {
  constructor(storage,env){this.storage=storage;this.env=env;}
  configured(){return /^\d{17,20}$/.test(this.env.DISCORD_CLIENT_ID||'')&&!!this.env.DISCORD_CLIENT_SECRET;}
  async start(request){
    if(!this.configured())fail('Discord ещё не настроен владельцем сервера',503);
    const b=await body(request);if(!/^[a-f0-9]{64}$/.test(b.challenge||''))fail('Некорректный запрос входа');
    const now=Date.now(), key='rate:'+await hash(request.headers.get('CF-Connecting-IP')||'local');
    const rate=await this.storage.get(key);if(rate&&rate.expires>now&&rate.count>=8)fail('Слишком много попыток входа. Подождите минуту',429);
    await this.storage.put(key,{count:rate?.expires>now?rate.count+1:1,expires:now+60000});
    const pending=await this.storage.list({prefix:'oauth:',limit:129});if(pending.size>=128)fail('Много ожидающих входов. Повторите позже',429);
    const id=random(),redirect=new URL('/oauth/callback',request.url).href;
    await this.storage.put('oauth:'+id,{challenge:b.challenge,redirect,expires:now+600000,status:'pending'});
    await this.storage.setAlarm(now+60000);
    const url=new URL('https://discord.com/oauth2/authorize');url.search=new URLSearchParams({client_id:this.env.DISCORD_CLIENT_ID,response_type:'code',redirect_uri:redirect,scope:'identify',state:id,prompt:'consent'});
    return json({id,url:url.href});
  }
  async callback(request){
    const url=new URL(request.url),id=url.searchParams.get('state')||'';
    if(!/^[a-f0-9]{64}$/.test(id))fail('Недействительный запрос входа');
    const key='oauth:'+id,p=await this.storage.get(key);
    if(!p||p.expires<Date.now()||p.status!=='pending')fail('Запрос истёк или уже использован. Повторите вход в приложении');
    p.status='failed';await this.storage.put(key,p);
    if(url.searchParams.has('error'))return this.page('Вход отменён. Вернитесь в DriftStart и повторите вход.');
    const code=url.searchParams.get('code');if(!code||code.length>2048)fail('Discord не вернул код авторизации');
    const response=await fetch('https://discord.com/api/oauth2/token',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({client_id:this.env.DISCORD_CLIENT_ID,client_secret:this.env.DISCORD_CLIENT_SECRET,grant_type:'authorization_code',code,redirect_uri:p.redirect}),signal:AbortSignal.timeout(8000)});
    if(!response.ok){
      let code='';try{code=(await response.json()).error;}catch{}
      const reasons={invalid_client:'Неверный Client Secret или он принадлежит другому приложению. Нужен OAuth2 Client Secret приложения '+this.env.DISCORD_CLIENT_ID,invalid_grant:'Код входа истёк/уже использован либо Redirect URI не совпадает. Начните новый вход.',invalid_request:'Discord отклонил параметры OAuth-запроса.',invalid_scope:'Discord отклонил разрешение identify.'};
      p.error=reasons[code]||`Discord временно отклонил обмен кода (HTTP ${response.status}). Повторите вход позже.`;
      await this.storage.put(key,p);fail(p.error,502);
    }
    const token=await response.json();
    if(typeof token.access_token!=='string')fail('Некорректный ответ Discord',502);
    const res=await fetch('https://discord.com/api/v10/users/@me',{headers:{Authorization:'Bearer '+token.access_token},signal:AbortSignal.timeout(8000)});
    if(!res.ok)fail('Не удалось получить профиль Discord',502);
    const profile=await res.json();if(!/^\d{17,20}$/.test(profile.id)||typeof profile.username!=='string')fail('Некорректный профиль Discord',502);
    // Discord access and refresh tokens are never stored or sent to the desktop.
    const existing=await this.storage.get('user:'+profile.id);
    if(!existing&&(await this.storage.list({prefix:'user:',limit:257})).size>=256)fail('Достигнут лимит 256 аккаунтов комнаты');
    const user={...existing,id:profile.id,username:profile.username.slice(0,32),name:(profile.global_name||profile.username).slice(0,40),role:existing?.role||'player',lastLogin:Date.now()};
    await this.storage.put('user:'+user.id,user);p.status='complete';p.userId=user.id;await this.storage.put(key,p);
    return this.page('Вход подтверждён. Вернитесь в DriftStart — приложение подключится автоматически. Эту вкладку можно закрыть.');
  }
  page(text){return new Response(`<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>DriftStart · Discord</title><body style="background:#0c121b;color:#ecf2f7;font:20px system-ui;padding:12vh 12vw"><h1 style="color:#74e5e0">DRIFTSTART</h1><p>${text}</p></body></html>`,{headers:{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'",'X-Content-Type-Options':'nosniff'}});}
  async claim(request){
    const b=await body(request);if(!/^[a-f0-9]{64}$/.test(b.id||'')||!/^[a-f0-9]{64}$/.test(b.verifier||''))fail('Некорректный запрос');
    const key='oauth:'+b.id,p=await this.storage.get(key);
    if(!p||p.expires<Date.now())fail('Время входа истекло. Повторите вход',410);
    if(!await matches(await hash(b.verifier),p.challenge))fail('Неверное подтверждение входа',403);
    if(p.status==='failed'){await this.storage.delete(key);fail(p.error||'Вход отменён или отклонён Discord. Повторите вход');}
    if(p.status!=='complete')return json({status:'pending'},202);
    const user=await this.storage.get('user:'+p.userId),token=random(),sessionHash=await hash(token),expires=Date.now()+7*86400000;
    if(user.sessionHash)await this.storage.delete('session:'+user.sessionHash);
    user.sessionHash=sessionHash;
    await this.storage.put('user:'+user.id,user);
    await this.storage.put('session:'+sessionHash,{userId:user.id,expires});await this.storage.delete(key);
    return json({status:'complete',token,expires,user:publicUser(user)});
  }
  async authenticate(request){
    const token=(request.headers.get('Authorization')||'').replace(/^Bearer /,'');
    if(!/^[a-f0-9]{64}$/.test(token))fail('Войдите через Discord',401);
    return this.byHash(await hash(token));
  }
  async byHash(sessionHash){
    const session=await this.storage.get('session:'+sessionHash);
    if(!session||session.expires<Date.now())fail('Сессия истекла. Войдите через Discord повторно',401);
    const user=await this.storage.get('user:'+session.userId);
    if(!user||user.sessionHash!==sessionHash)fail('Сессия закрыта. Войдите повторно',401);
    return user;
  }
  async cleanup(){for(const prefix of ['oauth:','session:','rate:']){const rows=await this.storage.list({prefix});for(const [key,value]of rows)if(value.expires<Date.now())await this.storage.delete(key);}await this.storage.setAlarm(Date.now()+3600000);}
}
