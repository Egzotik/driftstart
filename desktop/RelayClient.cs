using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DriftStart;

public record PeerView(string Id,string Name,bool Ready,double AgeMs,bool Received,string? UserId=null,string? Username=null,string? Nickname=null,string? StaticId=null,string? VehicleName=null,string? VehicleImage=null);
public record Snapshot(double ServerMs,string Instance,long Version,int ReadyEpoch,string Label,double? StartAt,bool Seated,PeerView[] Peers,Account? User=null,int CountdownMs=6000);
public record ConnectionOptions(string Url, string RoomCode, string Name, bool Judge, string JudgeKey,string Token="");
public record ClockInfo(bool Synced, double Offset, double Rtt);

public sealed class RelayClient : IDisposable
{
    static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Now => Watch.Elapsed.TotalMilliseconds;
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly object clockGate = new();
    readonly List<(double rtt, double offset, double seen)> samples = [];
    readonly CancellationTokenSource stop = new();
    readonly ConnectionOptions options;
    ClientWebSocket? socket;
    long ack = -1; string? instance; long lastReceived;
    public Snapshot? State { get; private set; }
    public bool Connected => socket?.State == WebSocketState.Open && Now - Interlocked.Read(ref lastReceived) < 2500;
    public bool Running => !stop.IsCancellationRequested;
    public event Action<Snapshot>? StateChanged;
    public event Action<string>? Notice;
    public event Action? Disconnected;
    public event Action? DataChanged;
    public RelayClient(ConnectionOptions options) { this.options = options; }
    public ClockInfo Clock
    {
        get { lock (clockGate) { if (samples.Count < 3 || Now - samples[^1].seen > 5000) return new(false, 0, 0); var best = samples.MinBy(s => s.rtt); return new(best.rtt <= 500, best.offset, best.rtt); } }
    }
    public void Start() => _ = Task.Run(Run);
    async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket(); socket = ws;
            lock(clockGate) samples.Clear(); State = null; ack = -1; instance = null;
            ws.Options.SetRequestHeader("X-Room-Code", options.RoomCode);
            ws.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            try
            {
                var uri = new UriBuilder(options.Url) { Scheme = options.Url.StartsWith("https:") ? "wss" : "ws", Path = "/ws", Query = "", Fragment = "" };
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); connectTimeout.CancelAfter(10000);
                await ws.ConnectAsync(uri.Uri, connectTimeout.Token);
                Interlocked.Exchange(ref lastReceived, (long)Now);
                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var ping = PingLoop(ws, connectionStop.Token);
                try { await Receive(ws, connectionStop.Token); }
                finally { connectionStop.Cancel(); try { await ping; } catch (OperationCanceledException) {} }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or System.Net.Http.HttpRequestException or JsonException or InvalidOperationException)
            {
                if (!stop.IsCancellationRequested) Notice?.Invoke("Нет подключения. Проверьте сеть; если сессия Discord истекла, выйдите из аккаунта и войдите снова. Повтор через 2 секунды.");
            }
            finally { ws.Abort(); State = null; lock(clockGate) samples.Clear(); Disconnected?.Invoke(); }
            try { await Task.Delay(2000, stop.Token); } catch(OperationCanceledException) { break; }
        }
    }
    async Task PingLoop(ClientWebSocket ws, CancellationToken token)
    {
        int sent = 0;
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            if (Now - Interlocked.Read(ref lastReceived) > 4000) { ws.Abort(); break; }
            await Send(new { type = "ping", nonce = Now }, token);
            await Task.Delay(sent++ < 4 ? 250 : 750, token);
        }
    }
    async Task Receive(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[65536];
        while(ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            int count = 0; WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length-count), token);
                if(result.MessageType == WebSocketMessageType.Close) return;
                count += result.Count;
                if(count >= buffer.Length && !result.EndOfMessage) throw new WebSocketException("State frame too large");
            } while (!result.EndOfMessage);
            Interlocked.Exchange(ref lastReceived, (long)Now);
            using var document = JsonDocument.Parse(buffer.AsMemory(0,count)); var m = document.RootElement;
            var type = m.GetProperty("type").GetString();
            if(type == "pong")
            {
                double t1=Now, t0=m.GetProperty("nonce").GetDouble(), remote=m.GetProperty("serverMs").GetDouble();
                if(t1>=t0 && t1-t0<5000) lock(clockGate) { samples.RemoveAll(s => t1-s.seen>15000); samples.Add((t1-t0,remote-(t0+t1)/2,t1)); }
                await Apply(m.GetProperty("state"),token);
            }
            else if(type == "state") await Apply(m,token);
            else if(type == "changed") DataChanged?.Invoke();
            else if(type == "error") Notice?.Invoke(m.GetProperty("error").GetString() ?? "Команда отклонена");
            else if(type == "result") Notice?.Invoke(m.GetProperty("action").GetString() == "start" ? "Старт назначен. Проверьте подтверждения пилотов." : "Команда выполнена. Перед следующим стартом нужна новая готовность.");
        }
    }
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    async Task Apply(JsonElement json, CancellationToken token)
    {
        var next=json.Deserialize<Snapshot>(JsonOptions); if(next==null)return;
        if(instance!=null && instance!=next.Instance) { lock(clockGate)samples.Clear(); ack=-1; }
        instance=next.Instance; State=next; StateChanged?.Invoke(next);
        if(ack!=next.Version) { ack=next.Version; await Send(new {type="ack",version=ack},token); }
    }
    public Task Ready(bool value) => Send(new { type="ready", ready=value, epoch=State?.ReadyEpoch ?? -1 });
    public Task Control(string action,string label,int delay=6000) => Send(new {type="control",action,label,delayMs=delay,requestId=Guid.NewGuid().ToString("N")});
    public async Task Send(object value, CancellationToken token=default)
    {
        var ws=socket; if(ws?.State!=WebSocketState.Open)return;
        await sendGate.WaitAsync(token);
        try { if(ws.State==WebSocketState.Open) await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),WebSocketMessageType.Text,true,token); }
        catch(Exception e) when(e is WebSocketException or OperationCanceledException or ObjectDisposedException) { ws.Abort(); }
        finally { sendGate.Release(); }
    }
    public void Dispose() { stop.Cancel(); socket?.Abort(); }
}
