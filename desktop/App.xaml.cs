using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DriftStart;
public partial class App : Application
{
    public static bool Preview;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if(await UpdateService.ApplyIncoming(e.Args)){Shutdown();return;}base.OnStartup(e); Preview=e.Args.Contains("--preview")||e.Args.Contains("--profile-preview")||e.Args.Contains("--hud-preview")||e.Args.Contains("--verify")||e.Args.Contains("--event-preview")||e.Args.Contains("--bracket-preview");
        var window=new MainWindowV4();MainWindow=window;
        window.Loaded+=async(_,_)=>
        {
            if(e.Args.Length==2&&e.Args[0]=="--preview"){window.Demo();await Task.Delay(180);Save(window,e.Args[1]);Shutdown();}
            else if(e.Args.Length==2&&e.Args[0]=="--profile-preview"){window.DemoProfile();await Task.Delay(1800);Save(window,e.Args[1]);Shutdown();}
            else if(e.Args.Length==2&&(e.Args[0]=="--event-preview"||e.Args[0]=="--bracket-preview")){window.Hide();using var api=new CloudApi(Defaults.ServerUrl,"preview");var events=new EventWindow(api);events.Show();events.Demo(e.Args[0]=="--event-preview"?"qualification":"bracket");await Task.Delay(200);Save(events,e.Args[1]);events.Close();Shutdown();}
            else if(e.Args.Length==2&&e.Args[0]=="--hud-preview"){var hud=new Hud();hud.Show();hud.Display("СТАРТ","Пара 12 / проезд 1",5,"ЕДИНОЕ ВРЕМЯ. ОБЩИЙ СТАРТ.");await Task.Delay(180);Save(hud,e.Args[1]);hud.Close();Shutdown();}
            else if(e.Args.Length==2&&e.Args[0]=="--verify"){window.Hide();await ClientVerification.Run(e.Args[1]);Shutdown();}
            else { }
        };
        window.Show();
    }
    static void Save(Window w,string path){w.UpdateLayout();var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(w);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var f=File.Create(path);png.Save(f);}
}
