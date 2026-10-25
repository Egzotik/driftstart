using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace DriftStart;
public sealed class GlobalHotkeys:IDisposable
{
 const int WmHotkey=0x0312,StartId=0x445301,CancelId=0x445302,StartHudId=0x445303,JudgeHudId=0x445304,OrderHudId=0x445305; const uint ModNoRepeat=0x4000;
 [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(IntPtr hWnd,int id,uint modifiers,uint key);
 [DllImport("user32.dll")]static extern bool UnregisterHotKey(IntPtr hWnd,int id);
 IntPtr handle;HwndSource? source;
 public event Action? StartPressed;public event Action? CancelPressed;public event Action? StartHudPressed;public event Action? JudgeHudPressed;public event Action? OrderHudPressed;
 public bool StartRegistered{get;private set;} public bool CancelRegistered{get;private set;}
 public bool Register(Window window,Key startKey=Key.F8,Key cancelKey=Key.F7,Key startHudKey=Key.F6,Key judgeHudKey=Key.F5,Key orderHudKey=Key.F4)
 {
  handle=new WindowInteropHelper(window).Handle;source??=HwndSource.FromHwnd(handle);source.RemoveHook(Hook);source.AddHook(Hook);foreach(var id in new[]{StartId,CancelId,StartHudId,JudgeHudId,OrderHudId})UnregisterHotKey(handle,id);
  StartRegistered=RegisterHotKey(handle,StartId,ModNoRepeat,(uint)KeyInterop.VirtualKeyFromKey(startKey));
  CancelRegistered=RegisterHotKey(handle,CancelId,ModNoRepeat,(uint)KeyInterop.VirtualKeyFromKey(cancelKey));
  return StartRegistered&&CancelRegistered&&RegisterHotKey(handle,StartHudId,ModNoRepeat,(uint)KeyInterop.VirtualKeyFromKey(startHudKey))&&RegisterHotKey(handle,JudgeHudId,ModNoRepeat,(uint)KeyInterop.VirtualKeyFromKey(judgeHudKey))&&RegisterHotKey(handle,OrderHudId,ModNoRepeat,(uint)KeyInterop.VirtualKeyFromKey(orderHudKey));
 }
 IntPtr Hook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){if(msg!=WmHotkey)return IntPtr.Zero;handled=true;int id=wParam.ToInt32();if(id==StartId)StartPressed?.Invoke();else if(id==CancelId)CancelPressed?.Invoke();else if(id==StartHudId)StartHudPressed?.Invoke();else if(id==JudgeHudId)JudgeHudPressed?.Invoke();else if(id==OrderHudId)OrderHudPressed?.Invoke();return IntPtr.Zero;}
 public void Dispose(){if(handle!=IntPtr.Zero)foreach(var id in new[]{StartId,CancelId,StartHudId,JudgeHudId,OrderHudId})UnregisterHotKey(handle,id);source?.RemoveHook(Hook);}
}
