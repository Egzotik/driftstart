using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DriftStart;
public sealed class LegacyHud : Window
{
    readonly Ellipse[] lamps=new Ellipse[3];
    readonly TextBlock signal=new(){FontSize=32,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock label=new(){FontSize=10,Foreground=Paint.Brush("#8497AD")};
    readonly TextBlock note=new(){FontSize=10,Foreground=Paint.Brush("#A0B0C1")};
    readonly Border box; bool movable;
    public int ActiveLight {get;private set;}=-1;
    public string Signal=>signal.Text;
    public LegacyHud()
    {
        Title="DriftStart HUD";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;ShowActivated=false;Width=470;Height=136;
        box=new(){Width=470,Height=136,Background=Paint.Brush("#F2111B27"),BorderBrush=Paint.Brush("#3A5265"),BorderThickness=new(1),CornerRadius=new(18),Padding=new(23,16,23,14)};
        Content=box;var layout=new Grid();box.Child=layout;
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.Children.Add(label);
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});Grid.SetRow(row,1);layout.Children.Add(row);
        var lights=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};row.Children.Add(lights);
        for(int i=0;i<3;i++){lamps[i]=new(){Width=29,Height=29,Fill=Paint.Brush("#2A3746"),Margin=new(0,0,9,0)};lights.Children.Add(lamps[i]);}
        signal.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(signal,1);row.Children.Add(signal);
        Grid.SetRow(note,2);layout.Children.Add(note);
        SourceInitialized+=(_,_)=>SetInputMode();MouseLeftButtonDown+=(_,e)=>{if(movable&&e.ButtonState==MouseButtonState.Pressed)DragMove();};
        Center();Display("ОЖИДАНИЕ","DRIFTSTART / LIVE SESSION",-1,"ЖДЁМ СИГНАЛ СУДЬИ");
    }
    public void Center(){Left=(SystemParameters.PrimaryScreenWidth-Width)/2;Top=32;}
    public void Configure(double scale,double opacity,bool move){box.LayoutTransform=new ScaleTransform(scale,scale);Width=470*scale;Height=136*scale;Opacity=opacity;movable=move;SetInputMode();}
    void SetInputMode(){nint h=new WindowInteropHelper(this).Handle;if(h==0)return;int flags=GetWindowLong(h,-20)|0x80;flags=movable?flags&~(0x20|0x08000000):flags|0x20|0x08000000;SetWindowLong(h,-20,flags);}
    public void Display(string text,string title,int active,string hint)
    {
        string[] colors=["#FF6879","#FFCA69","#79E8B4"];ActiveLight=active;
        for(int i=0;i<3;i++)lamps[i].Fill=Paint.Brush(i==active?colors[i]:"#2A3746");
        signal.Text=text;signal.FontSize=text.Length>9?23:32;signal.Foreground=Paint.Brush(active>=0?colors[active]:"#C4D1DF");label.Text=title.ToUpperInvariant();note.Text=hint;
    }
    [DllImport("user32.dll")]static extern int GetWindowLong(nint h,int i);
    [DllImport("user32.dll")]static extern int SetWindowLong(nint h,int i,int value);
}
public static class Paint {public static SolidColorBrush Brush(string color)=>(SolidColorBrush)new BrushConverter().ConvertFromString(color)!;}
