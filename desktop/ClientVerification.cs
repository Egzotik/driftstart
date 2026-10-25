using System.IO;
using System.Text.Json;
namespace DriftStart;
public static class ClientVerification
{
 public static async Task Run(string report)
 {
  string url=Environment.GetEnvironmentVariable("DRIFT_SERVER_URL")!,room=Environment.GetEnvironmentVariable("DRIFT_ROOM_CODE")!;
  using var judge=new RelayClient(new(url,room,"Judge",true,"",Environment.GetEnvironmentVariable("DRIFT_TEST_JUDGE_TOKEN")!));using var a=new RelayClient(new(url,room,"Driver A",false,"",Environment.GetEnvironmentVariable("DRIFT_TEST_A_TOKEN")!));using var b=new RelayClient(new(url,room,"Driver B",false,"",Environment.GetEnvironmentVariable("DRIFT_TEST_B_TOKEN")!));
  var checks=new List<string>();
  async Task Until(Func<bool> fn,string name,int timeout=12000){double end=RelayClient.Now+timeout;while(!fn()){if(RelayClient.Now>end)throw new Exception(name+": timeout");await Task.Delay(20);}checks.Add(name);}
  try{
   var protectedToken=SecretCache.Protect("verification-only-token");if(SecretCache.Unprotect(protectedToken)!="verification-only-token")throw new Exception("DPAPI round trip failed");checks.Add("Windows DPAPI session protection round trip");
   judge.Start();a.Start();b.Start();
   await Until(()=>judge.Clock.Synced&&a.Clock.Synced&&b.Clock.Synced&&judge.State?.Peers.Length==2,"WSS clients connect and synchronize");
   await a.Ready(true);await b.Ready(true);
   await Until(()=>judge.State!.Peers.All(p=>p.Ready&&p.Received),"Judge receives readiness and acknowledgements");
   await judge.Control("start","Verification");
   await Until(()=>a.State?.StartAt!=null&&b.State?.StartAt==a.State.StartAt,"Clients receive same scheduled timestamp");
   await Until(()=>judge.State!.Peers.All(p=>p.Received),"Start delivery acknowledged by both drivers");
   long greenA=0,greenB=0;double deadline=RelayClient.Now+10000;
   while(greenA==0||greenB==0){if(RelayClient.Now>deadline)throw new Exception("Green timeout");if(greenA==0&&RelayClient.Now+a.Clock.Offset>=a.State!.StartAt)greenA=System.Diagnostics.Stopwatch.GetTimestamp();if(greenB==0&&RelayClient.Now+b.Clock.Offset>=b.State!.StartAt)greenB=System.Diagnostics.Stopwatch.GetTimestamp();await Task.Delay(5);}
   double skew=Math.Abs(System.Diagnostics.Stopwatch.GetElapsedTime(greenA,greenB).TotalMilliseconds);if(skew>250)throw new Exception("Local skew too large: "+skew);checks.Add("Both clients reach green within 250 ms locally");
   await judge.Control("cancel","");await Until(()=>a.State?.StartAt==null&&b.State?.StartAt==null,"Cancel reaches both clients");
   await Until(()=>judge.State!.Peers.All(p=>!p.Ready),"Cancellation invalidates prior readiness");
   b.Dispose();await Until(()=>!b.Connected,"Disconnected client loses live status");
   File.WriteAllText(report,JsonSerializer.Serialize(new{result="PASS",checks=checks.Count,localTransitionDifferenceMs=skew,details=checks},new JsonSerializerOptions{WriteIndented=true}));
  }catch(Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{result="FAIL",error=e.ToString(),passed=checks},new JsonSerializerOptions{WriteIndented=true}));}
 }
}
