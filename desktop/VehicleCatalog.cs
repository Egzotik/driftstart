namespace DriftStart;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
public record VehicleChoice(string Id,string Name,string? ExternalImage=null)
{
 public string ImageUrl=>ExternalImage??$"https://cdn.majestic-files.net/public/master/static/img/vehicles/{Id}.png";
 public override string ToString()=>Name;
}
public static class VehicleCatalog
{
 // Names and image identifiers follow the public Majestic RP vehicle catalog.
 public static readonly VehicleChoice[] All=[
  new("futo","Karin Futo"),new("futo2","Karin Futo GTX"),new("zr350","Annis ZR350"),new("sultan","Karin Sultan"),new("sultan2","Karin Sultan Classic"),new("sultan3","Karin Sultan RS Classic"),
  new("elegy","Annis Elegy Retro Custom"),new("elegy2","Annis Elegy RH8"),new("jester","Dinka Jester"),new("jester3","Dinka Jester Classic"),new("jester4","Dinka Jester RR"),new("remus","Annis Remus"),
  new("rt3000","Dinka RT3000"),new("euros","Annis Euros"),new("calico","Karin Calico GTF"),new("vectre","Emperor Vectre"),new("cypher","Ubermacht Cypher"),new("comet6","Pfister Comet S2"),
  new("sentinel","Ubermacht Sentinel XS"),new("sentinel2","Ubermacht Sentinel"),new("zion","Ubermacht Zion"),new("zion2","Ubermacht Zion Cabrio"),new("previon","Karin Previon"),new("kanjosj","Dinka Kanjo SJ"),
  new("asbo","Maxwell Asbo"),new("blista","Dinka Blista"),new("brioso","Grotti Brioso R/A"),new("kanjo","Dinka Blista Kanjo"),new("prairie","Bollokan Prairie"),new("weevil","BF Weevil"),
  new("schwarzer","Benefactor Schwartzer"),new("dominator","Vapid Dominator"),new("gauntlet","Bravado Gauntlet"),new("tampa2","Declasse Drift Tampa"),new("yosemite2","Declasse Drift Yosemite"),new("fr36","Fathom FR36")
 ];
 public static VehicleChoice[] Current{get;private set;}=All;
 public static async Task<VehicleChoice[]> LoadAll()
 {
  try{
   using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};
   var pages=await Task.WhenAll(Enumerable.Range(1,10).Select(page=>http.GetStringAsync("https://wiki.majestic-rp.ru/ru/vehicles/car?page="+page)));
   var found=new Dictionary<string,VehicleChoice>(StringComparer.OrdinalIgnoreCase);
   var pattern=new Regex("<div id=\"(?<id>[a-zA-Z0-9_-]+)\" class=\"relative flex flex-col.*?<img alt=\"(?<name>[^\"]+)\"[^>]+/vehicles/(?<image>[a-zA-Z0-9_-]+)\\.png",RegexOptions.Singleline|RegexOptions.Compiled);
   foreach(var html in pages)foreach(System.Text.RegularExpressions.Match match in pattern.Matches(html)){string id=match.Groups["image"].Value,name=WebUtility.HtmlDecode(match.Groups["name"].Value);if(id.Length>0&&name.Length>1)found[id]=new(id,name);}
   var russia=await http.GetStringAsync("https://wiki.russia.online/ru/vehicles");
   var russiaPattern=new Regex("<img[^>]+alt=\"(?<name>[^\"]+)\"[^>]+(?:src|data-src)=\"(?<url>[^\"]*(?:vehicles|vehicle|transport)[^\"]*?/(?<id>[a-zA-Z0-9_-]+)\\.(?:png|webp|jpg)[^\"]*)\"",RegexOptions.IgnoreCase|RegexOptions.Singleline);
   foreach(System.Text.RegularExpressions.Match match in russiaPattern.Matches(russia)){string id=match.Groups["id"].Value,name=WebUtility.HtmlDecode(match.Groups["name"].Value),url=WebUtility.HtmlDecode(match.Groups["url"].Value);if(url.StartsWith("/"))url="https://wiki.russia.online"+url;if(id.Length>0&&name.Length>1&&!found.Values.Any(x=>x.Name.Equals(name,StringComparison.CurrentCultureIgnoreCase)))found[id]=new(id,name,url);}
   if(found.Count>30)Current=found.Values.GroupBy(x=>Regex.Replace(x.Name.ToLowerInvariant(),"[^a-zа-я0-9]",""),StringComparer.OrdinalIgnoreCase).Select(x=>x.First()).OrderBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
  }catch(HttpRequestException){}catch(TaskCanceledException){}
  return Current;
 }
}
public record PlayerProfile(string Nickname,string StaticId,string VehicleId,string VehicleName,string? VehicleImage,double UpdatedAt);
