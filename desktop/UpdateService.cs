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
 public static async Task CheckAndOffer(Window owner,Action<string> status)
 {
  try{
   status("Проверяем обновления…");var json=await Http.GetStringAsync(Defaults.ServerUrl+"/update/manifest");var m=JsonSerializer.Deserialize<Manifest>(json);if(m==null)return;
   var current=Assembly.GetExecutingAssembly().GetName().Version??new Version();if(!Version.TryParse(m.Version,out var latest)||latest<=current){status($"DriftStart {current.Major}.{current.Minor}.{current.Build} · актуальная версия");return;}
   if(string.IsNullOrWhiteSpace(m.Url)||string.IsNullOrWhiteSpace(m.Sha256)){status("Обновление готовится к публикации.");return;}
   var answer=MessageBox.Show(owner,$"Доступен DriftStart {m.Version}.\n\n{m.Notes}\n\nСкачать и установить сейчас?","Обновление DriftStart",MessageBoxButton.YesNo,MessageBoxImage.Information);if(answer!=MessageBoxResult.Yes){status("Обновление отложено.");return;}
   status("Скачиваем обновление…");var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DriftStart","updates");Directory.CreateDirectory(dir);var file=Path.Combine(dir,$"DriftStart-{m.Version}.exe");await using(var input=await Http.GetStreamAsync(m.Url))await using(var output=File.Create(file)){await input.CopyToAsync(output);}
   await using var check=File.OpenRead(file);var actual=Convert.ToHexString(await SHA256.HashDataAsync(check));if(!actual.Equals(m.Sha256,StringComparison.OrdinalIgnoreCase)){File.Delete(file);throw new InvalidDataException("Контрольная сумма обновления не совпала.");}
   var target=Environment.ProcessPath??throw new InvalidOperationException("Не найден путь приложения.");var start=new ProcessStartInfo(file){UseShellExecute=true};start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(target);start.ArgumentList.Add(Environment.ProcessId.ToString());Process.Start(start);Application.Current.Shutdown();
  }catch(Exception ex){status("Автообновление недоступно: "+ex.Message);}
 }
 public static async Task<bool> ApplyIncoming(string[] args)
 {
  if(args.Length!=3||args[0]!="--apply-update")return false;if(!int.TryParse(args[2],out var pid))return true;
  try{var parent=Process.GetProcessById(pid);await parent.WaitForExitAsync();}catch{}
  await Task.Delay(500);try{File.Copy(Environment.ProcessPath!,args[1],true);Process.Start(new ProcessStartInfo(args[1]){UseShellExecute=true});}catch(Exception ex){MessageBox.Show("Не удалось заменить приложение: "+ex.Message,"DriftStart Update",MessageBoxButton.OK,MessageBoxImage.Error);}return true;
 }
}
