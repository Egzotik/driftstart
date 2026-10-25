# DriftStart 0.3 source

Independent implementation; no Miami Graphics launcher code or assets were copied.

## Desktop

Requires .NET SDK 10 on Windows. Build: `dotnet publish desktop/DriftStart.csproj -c Release -o artifacts/app`.
`scripts/Build-Release.ps1` optionally invokes Authenticode signing with a supplied certificate thumbprint. No publisher certificate is included.

WPF, self-contained win-x64 single-file executable. `CloudApi.cs` implements the browser OAuth handoff, HTTP APIs and Windows DPAPI token cache. `RelayClient.cs` synchronizes a monotonic clock over authenticated WebSockets. `EventWindow.cs` provides qualification, seeded elimination brackets, audit browsing/export and owner-controlled roles.

`--profile <private-room.json> --connect` imports a room and initiates sign-in. The first authenticated account holding the legacy organizer key can claim the owner role once. Never ship that private profile with public releases.

## Cloudflare

In `cloudflare`: `npm ci`, `npm test`, `npm run test:integration`, `npm run deploy`.
The production entry is `src/server.js`. `DriftRoom` is the existing SQLite-backed Durable Object; migration v1 must not be removed on an existing deployment.
Required secrets: `DRIFT_ROOM_CODE`, `DRIFT_JUDGE_KEY` (one-time owner bootstrap), `DISCORD_CLIENT_ID`, `DISCORD_CLIENT_SECRET`.
Register `https://<worker-host>/oauth/callback` in Discord's OAuth2 redirect settings. Scope: identify only. No bot token.

No unauthenticated or test login endpoint exists in production. Integration tests intercept outgoing Discord requests only inside Miniflare. Test storage is isolated under a generated `integration-state-*` directory. Do not deploy or package test state.

Run the actual EXE test by setting `DRIFT_EXE` to a built EXE before `npm run test:integration`. The harness supplies ephemeral test sessions via environment variables and launches `--verify`; it does not print session values.

OAuth state and proof-bound handoff expire after 10 minutes. App sessions expire after 7 days, are stored hashed server-side, and replace the previous session for that Discord account. HTTP actions and WebSocket messages verify current server roles. Owner assignment is single-use. Role revocation closes the target account's existing WebSocket.

Tournament writes are serialized in the Durable Object and require an expected revision. A single storage put writes tournament state, summary and the immutable audit record atomically. Rejected changes operate on a cloned domain model. Audit history is stored one row per revision; APIs paginate at 200 records.

Storage prefixes: `user:`, `session:`, `oauth:`, `rate:`, `event:`, `event-index:`, `audit:`, `rolelog:`; singleton keys `ownerId` and legacy `room`. Expired sessions/OAuth/rate entries are cleaned by an alarm. Tournament and role audit records are retained; there is no automatic deletion or backup policy. Export tournament JSON from the app for a separate copy.

Limits in this version: one room, 64 connected pilots/68 total sockets, 256 registered Discord accounts. Qualification uses two 0–100 attempts and registration order as the final full-tie breaker. Archive is read-only. There is no self-service ownership transfer/recovery UI.

## References

- Discord OAuth2: https://docs.discord.com/developers/topics/oauth2
- Current user: https://docs.discord.com/developers/resources/user#get-current-user
- Cloudflare Durable Object storage: https://developers.cloudflare.com/durable-objects/api/legacy-kv-storage-api/
