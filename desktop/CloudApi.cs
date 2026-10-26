using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DriftStart;
public record Account(string Id,string Name,string Username,string Role,PlayerProfile? Profile=null,string? Discipline=null)
{
    public string RoleLabel=>Role switch{"leader"=>"Лидер","cohost"=>"Помощник","judge"=>"Судья · "+DisciplineLabel(Discipline),_=>"Пилот"};
    public static string DisciplineLabel(string? value)=>value switch{"trajectory"=>"траектория","angle"=>"угол","style"=>"стиль",_=>"дисциплина не назначена"};
    public override string ToString()=>$"{Profile?.Nickname??Name}"+(Profile?.StaticId is string id?$"  #{id}":"")+$" · {RoleLabel}";
}
public sealed class ApiException(string message,int status):Exception(message){public int Status{get;}=status;}
public sealed class CloudApi:IDisposable
{
    readonly HttpClient client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(25)};
    readonly string cachePath;
    public string Url{get;} public string Room{get;private set;}=""; public string Token{get;private set;}="";
    public Account? Me{get;private set;}
    public static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public CloudApi(string url,string room)
    {
        Url=url;Room=room;client.BaseAddress=new Uri(url);
        string folder=Environment.GetEnvironmentVariable("DRIFT_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DriftStart");
        cachePath=Path.Combine(folder,"session-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..20]+".bin");
        if(!App.Preview)try{if(File.Exists(cachePath))Token=SecretCache.Unprotect(File.ReadAllBytes(cachePath));}catch(Exception ex)when(ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){}
    }
    public async Task<JsonElement> Request(string path,object? value=null,CancellationToken token=default)
    {
        using var req=new HttpRequestMessage(value==null?HttpMethod.Get:HttpMethod.Post,path);
        if(Room.Length>0)req.Headers.Add("X-Room-Code",Room);
        if(Token.Length>0)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Token);
        if(value!=null)req.Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json");
        using var res=await client.SendAsync(req,token);
        var raw=await res.Content.ReadAsStringAsync(token);JsonElement result;
        try{result=JsonSerializer.Deserialize<JsonElement>(raw);}catch(JsonException){throw new ApiException("Сервер вернул неожиданный ответ",(int)res.StatusCode);}
        if(!res.IsSuccessStatusCode)throw new ApiException(result.TryGetProperty("error",out var e)?e.GetString()??"Ошибка сервера":"Ошибка сервера",(int)res.StatusCode);
        return result;
    }
    public async Task RefreshMe(CancellationToken token=default){var r=await Request("/api/me",token:token);Me=r.GetProperty("user").Deserialize<Account>(JsonOptions);}
    public async Task<bool> RestoreSession(CancellationToken token)
    {
        if(Token.Length==0)return false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try{await RefreshMe(timeout.Token);return true;}
        catch(ApiException ex)when(ex.Status==401){Forget();return false;}
    }
    public async Task Login(Action<string> notice,CancellationToken token)
    {
        if(Token.Length>0){try{await RefreshMe(token);return;}catch(ApiException ex)when(ex.Status==401){Forget();}}
        string verifier=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string challenge=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))).ToLowerInvariant();
        var start=await Request("/auth/start",new{challenge},token);string url=start.GetProperty("url").GetString()!,id=start.GetProperty("id").GetString()!;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="discord.com"||uri.AbsolutePath!="/oauth2/authorize")throw new ApiException("Сервер вернул неправильный адрес Discord",400);
        try{Process.Start(new ProcessStartInfo("microsoft-edge:"+url){UseShellExecute=true});}catch(System.ComponentModel.Win32Exception){Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
        notice("Подтвердите вход в Discord в браузере Edge. Ожидаем до 10 минут…");
        for(int i=0;i<300;i++){
            await Task.Delay(2000,token);var r=await Request("/auth/claim",new{id,verifier},token);
            if(r.GetProperty("status").GetString()!="complete")continue;
            Token=r.GetProperty("token").GetString()!;
            if(!App.Preview){Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);File.WriteAllBytes(cachePath,SecretCache.Protect(Token));}
            await RefreshMe(token);return;
        }
        throw new ApiException("Время входа истекло. Повторите вход",408);
    }
    public void SelectRoom(string room)=>Room=room.Trim().ToUpperInvariant();
    internal void PreviewAccount(Account account){if(!App.Preview)throw new InvalidOperationException("Preview only");Me=account;}
    public void SetRoomRole(string role){if(Me!=null)Me=Me with{Role=role};}
    public async Task Logout(){try{if(Token.Length>0)await Request("/api/logout",new{});}finally{Forget();}}
    void Forget(){Token="";Me=null;try{File.Delete(cachePath);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public void Dispose()=>client.Dispose();
}
static class SecretCache
{
    [StructLayout(LayoutKind.Sequential)]struct Blob{public int Length;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr value);
    static byte[] Convert(byte[] value,bool protect){var input=new Blob{Length=value.Length,Data=Marshal.AllocHGlobal(value.Length)};Blob output=default;
        try{Marshal.Copy(value,0,input.Data,value.Length);bool ok=protect?CryptProtectData(ref input,"DriftStart session",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);if(!ok)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;}
        finally{Marshal.FreeHGlobal(input.Data);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);}}
    public static byte[] Protect(string value)=>Convert(Encoding.UTF8.GetBytes(value),true);
    public static string Unprotect(byte[] value)=>Encoding.UTF8.GetString(Convert(value,false));
}
