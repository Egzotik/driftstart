using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DriftStart;
public sealed class LegacyJudgeHud:Window
{
 readonly TextBlock pilot=new(){FontSize=22,FontWeight=FontWeights.Bold};readonly TextBlock meta=new(){Foreground=Paint.Brush("#A0A0A5"),FontSize=11};readonly TextBlock scores=new(){FontSize=30,FontWeight=FontWeights.Black,Foreground=Paint.Brush("#F20D4F")};
 public LegacyJudgeHud(){Title="DriftStart Judge HUD";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;ShowActivated=false;Width=430;Height=120;var box=new Border{Background=Paint.Brush("#F20A0A0C"),BorderBrush=Paint.Brush("#F20D4F"),BorderThickness=new(3,0,0,0),Padding=new(20,14,20,14)};var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var left=new StackPanel();left.Children.Add(new TextBlock{Text="QUALIFICATION / JUDGE",Foreground=Paint.Brush("#F20D4F"),FontSize=9,FontWeight=FontWeights.Bold});left.Children.Add(pilot);left.Children.Add(meta);grid.Children.Add(left);Grid.SetColumn(scores,1);scores.VerticalAlignment=VerticalAlignment.Center;grid.Children.Add(scores);box.Child=grid;Content=box;Left=SystemParameters.PrimaryScreenWidth-Width-32;Top=32;}
 public void Display(Entry p){pilot.Text=p.Name;meta.Text=$"#{p.StaticId??"—"}  ·  {p.VehicleName??"МАШИНА НЕ УКАЗАНА"}";scores.Text=$"{p.Scores[0]?.ToString("0.##")??"—"}  /  {p.Scores[1]?.ToString("0.##")??"—"}";if(!IsVisible)Show();}
}
