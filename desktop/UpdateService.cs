using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace DriftStart;
public static class UpdateService
{
 sealed record Manifest([property:JsonPropertyName("version")]string Version,[property:JsonPropertyName("url")]string? Url,[property:JsonPropertyName("sha256")]string? Sha256,[property:JsonPropertyName("notes")]string? Notes);
 static readonly HttpClient Http=new(){Timeout=TimeSpan.FromMinutes(10)};
 static string? deferredVersion;
 public static async Task CheckAndOffer(Window owner,Action<string> status,Action<bool,double,string>? progress=null)
 {
  try{
   status("Проверяем обновления…");var json=await Http.GetStringAsync(Defaults.ServerUrl+"/update/manifest");var m=JsonSerializer.Deserialize<Manifest>(json);if(m==null)return;
   var current=Assembly.GetExecutingAssembly().GetName().Version??new Version();if(!Version.TryParse(m.Version,out var latest)||latest<=current){status($"DriftStart {current.Major}.{current.Minor}.{current.Build} · актуальная версия");return;}
   if(deferredVersion==m.Version){status($"DriftStart {m.Version} будет предложен после следующего запуска.");return;}
   if(string.IsNullOrWhiteSpace(m.Url)||string.IsNullOrWhiteSpace(m.Sha256)){status("Обновление готовится к публикации.");return;}
   var answer=MessageBox.Show(owner,$"Доступен DriftStart {m.Version}.\n\n{m.Notes}\n\nДа — обновить сейчас.\nНет — продолжить работу и напомнить после следующего запуска.","Обновление DriftStart",MessageBoxButton.YesNo,MessageBoxImage.Information);if(answer!=MessageBoxResult.Yes){deferredVersion=m.Version;status("Обновление отложено до следующего запуска.");return;}
   status("Скачиваем обновление…");progress?.Invoke(true,0,$"Скачиваем DriftStart {m.Version}…");var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DriftStart","updates");Directory.CreateDirectory(dir);var file=Path.Combine(dir,$"DriftStart-{m.Version}.exe");using(var response=await Http.GetAsync(m.Url,HttpCompletionOption.ResponseHeadersRead)){response.EnsureSuccessStatusCode();var length=response.Content.Headers.ContentLength;await using var input=await response.Content.ReadAsStreamAsync();await using var output=File.Create(file);var buffer=new byte[1024*128];long read=0;int count;while((count=await input.ReadAsync(buffer))>0){await output.WriteAsync(buffer.AsMemory(0,count));read+=count;if(length>0)progress?.Invoke(true,Math.Clamp(read*100d/length.Value,0,100),$"Скачиваем DriftStart {m.Version}…");}}
   progress?.Invoke(true,100,"Проверяем и устанавливаем обновление…");
   await using var check=File.OpenRead(file);var actual=Convert.ToHexString(await SHA256.HashDataAsync(check));if(!actual.Equals(m.Sha256,StringComparison.OrdinalIgnoreCase)){File.Delete(file);throw new InvalidDataException("Контрольная сумма обновления не совпала.");}
   var target=Environment.ProcessPath??throw new InvalidOperationException("Не найден путь приложения.");var start=new ProcessStartInfo(file){UseShellExecute=true};start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(target);start.ArgumentList.Add(Environment.ProcessId.ToString());Process.Start(start);Application.Current.Shutdown();
  }catch(Exception ex){progress?.Invoke(false,0,"");status("Автообновление недоступно: "+ex.Message);}
 }
 public static async Task<bool> ApplyIncoming(string[] args)
 {
  if(args.Length!=3||args[0]!="--apply-update")return false;if(!int.TryParse(args[2],out var pid))return true;
  try{var parent=Process.GetProcessById(pid);await parent.WaitForExitAsync();}catch{}
  await Task.Delay(500);try{File.Copy(Environment.ProcessPath!,args[1],true);Process.Start(new ProcessStartInfo(args[1]){UseShellExecute=true});}catch(Exception ex){MessageBox.Show("Не удалось заменить приложение: "+ex.Message,"DriftStart Update",MessageBoxButton.OK,MessageBoxImage.Error);}return true;
 }
}
