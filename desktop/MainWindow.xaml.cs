using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DriftStart;
public partial class MainWindow : Window
{
    RelayClient? relay;Hud? hud;Snapshot? state;bool suppressReady,judge,demo;
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(16)};
    readonly string settingsPath=Path.Combine(Environment.GetEnvironmentVariable("DRIFT_CONFIG_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DriftStart"),"settings.json");
    public MainWindow()
    {
        InitializeComponent();UrlInput.Text=Defaults.ServerUrl;
        LoadSettings();
        UrlInput.Text=Environment.GetEnvironmentVariable("DRIFT_SERVER_URL")??UrlInput.Text;
        RoomInput.Text=Environment.GetEnvironmentVariable("DRIFT_ROOM_CODE")??"";
        NameInput.Text=Environment.GetEnvironmentVariable("DRIFT_NAME")??NameInput.Text;
        RoleJudge.IsChecked=Environment.GetEnvironmentVariable("DRIFT_ROLE")=="judge";
        if(RoleJudge.IsChecked!=true)RolePlayer.IsChecked=true;
        JudgeKeyInput.Password=Environment.GetEnvironmentVariable("DRIFT_JUDGE_KEY")??"";
        ShowPage("connect");timer.Tick+=(_,_)=>Tick();timer.Start();
        Closed+=(_,_)=>{loginStop?.Cancel();api?.Dispose();timer.Stop();relay?.Dispose();hud?.Close();if(!App.Preview)SaveSettings();};
    }
    void LoadSettings(){if(App.Preview)return;try{if(File.Exists(settingsPath)){var s=JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(settingsPath));if(s!=null){UrlInput.Text=s.ServerUrl;NameInput.Text=s.Name;HudScale.Value=Math.Clamp(s.Scale,.7,1.5);HudOpacity.Value=Math.Clamp(s.Opacity,.45,1);}}}catch(IOException){}catch(JsonException){}}
    void SaveSettings(){try{Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);File.WriteAllText(settingsPath,JsonSerializer.Serialize(new LocalSettings(UrlInput.Text.Trim(),NameInput.Text.Trim(),HudScale.Value,HudOpacity.Value)));}catch(IOException){}catch(UnauthorizedAccessException){}}
    record LocalSettings(string ServerUrl,string Name,double Scale,double Opacity);
    void ShowPage(string page)
    {
        RacePage.Visibility=page=="race"?Visibility.Visible:Visibility.Collapsed;ConnectPage.Visibility=page=="connect"?Visibility.Visible:Visibility.Collapsed;HudPage.Visibility=page=="hud"?Visibility.Visible:Visibility.Collapsed;
        PageTitle.Text=page=="race"?"Стартовая вышка":page=="connect"?"Подключение":"Настройки HUD";
        NavRace.Background=Paint.Brush(page=="race"?"#22353E":"#101721");NavConnect.Background=Paint.Brush(page=="connect"?"#22353E":"#101721");NavHud.Background=Paint.Brush(page=="hud"?"#22353E":"#101721");
    }
    void RacePage_Click(object sender,RoutedEventArgs e)=>ShowPage("race");
    void ConnectPage_Click(object sender,RoutedEventArgs e)=>ShowPage("connect");
    void HudPage_Click(object sender,RoutedEventArgs e)=>ShowPage("hud");
    void Role_Changed(object sender,RoutedEventArgs e){if(JudgeKeyPanel!=null)JudgeKeyPanel.Visibility=RoleJudge.IsChecked==true?Visibility.Visible:Visibility.Collapsed;}
    void Connect_Click(object sender,RoutedEventArgs e){if(relay!=null){Disconnect();return;}AutoConnect();}
    CloudApi? api; EventWindow? events; CancellationTokenSource? loginStop;
    public async void AutoConnect()
    {
        if(loginStop!=null){loginStop.Cancel();return;}
        string address=UrlInput.Text.Trim().TrimEnd('/');
        if(!Uri.TryCreate(address,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&!(uri.Scheme=="http"&&uri.IsLoopback))||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||uri.AbsolutePath!="/"){Message.Text="Нужен HTTPS-адрес сервера";return;}
        if(RoomInput.Text.Trim().Length<6){Message.Text="Введите код комнаты или откройте файл доступа.";return;}
        events?.Close();events=null;api?.Dispose();api=new CloudApi(address,RoomInput.Text.Trim());loginStop=new();ConnectButton.Content="Отменить вход";
        UrlInput.IsEnabled=RoomInput.IsEnabled=false;
        try {
            await api.Login(text=>Message.Text=text,loginStop.Token);
            if(!api.OwnerBound&&JudgeKeyInput.Password.Length>=16)await api.Bootstrap(JudgeKeyInput.Password);
            JudgeKeyInput.Clear();NameInput.Text=$"{api.Me!.Name} · {api.Me.RoleLabel}";SetRole(api.Me.Role);
            relay=new(new(address,RoomInput.Text.Trim(),api.Me.Name,judge,"",api.Token));var connection=relay;
            connection.StateChanged+=next=>Dispatcher.BeginInvoke(()=>{if(relay==connection)Apply(next);});
            connection.Notice+=text=>Dispatcher.BeginInvoke(()=>{if(relay==connection)Message.Text=text;});
            connection.Disconnected+=()=>Dispatcher.BeginInvoke(()=>{if(relay==connection){SetReady(false);state=null;}});
            connection.DataChanged+=()=>Dispatcher.BeginInvoke(()=>{if(relay==connection)events?.RefreshFromSignal();});
            ConnectButton.Content="Отключиться";hud=new Hud();ApplyHudSettings();connection.Start();Message.Text="Подключаемся к комнате и сверяем часы…";ShowPage("race");
        } catch(OperationCanceledException){Message.Text="Вход отменён или истекло время ожидания.";}
        catch(Exception ex)when(ex is ApiException or System.Net.Http.HttpRequestException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or JsonException){Message.Text=ex.Message;}
        finally {loginStop?.Dispose();loginStop=null;if(relay==null){UrlInput.IsEnabled=RoomInput.IsEnabled=true;ConnectButton.Content="Войти через Discord  →";}}
    }
    void SetRole(string role){judge=role is "judge" or "owner";StartButton.Visibility=CancelButton.Visibility=PruneButton.Visibility=judge?Visibility.Visible:Visibility.Collapsed;ReadyCheck.Visibility=judge?Visibility.Collapsed:Visibility.Visible;RoundInput.IsEnabled=Delay6.IsEnabled=Delay10.IsEnabled=judge;}
    void Events_Click(object sender,RoutedEventArgs e){if(api?.Me==null){Message.Text="Сначала войдите через Discord.";ShowPage("connect");return;}if(events!=null){events.Activate();return;}events=new EventWindow(api){Owner=this};events.Closed+=(_,_)=>events=null;events.Show();}
    async void Logout_Click(object sender,RoutedEventArgs e){loginStop?.Cancel();if(loginStop!=null)return;Disconnect();if(api!=null){try{await api.Logout();Message.Text="Вы вышли из Discord в DriftStart.";}catch(Exception ex){Message.Text="Локальная сессия удалена. Сервер: "+ex.Message;}api.Dispose();api=null;}NameInput.Text="Войдите через Discord";}
    void Disconnect()
    {
        events?.Close();events=null;var old=relay;relay=null;old?.Dispose();state=null;SetReady(false);hud?.Close();hud=null;
        UrlInput.IsEnabled=RoomInput.IsEnabled=NameInput.IsEnabled=RoleJudge.IsEnabled=RolePlayer.IsEnabled=JudgeKeyInput.IsEnabled=true;
        ConnectButton.Content="Войти через Discord  →";ConnectionStatus.Text="Не подключено";ConnectionDot.Fill=Paint.Brush("#6F8095");SignalText.Text="—";SignalHint.Text="Подключитесь к комнате";Roster.Children.Clear();PilotCount.Text="0 участников";Message.Text="Подключение закрыто.";
    }
    void SetReady(bool value){suppressReady=true;ReadyCheck.IsChecked=value;suppressReady=false;}
    void Apply(Snapshot next)
    {
        if(next.User!=null){if((next.User.Role is "judge" or "owner")!=judge)events?.RefreshFromSignal();SetRole(next.User.Role);}if(state!=null&&state.ReadyEpoch!=next.ReadyEpoch)SetReady(false);state=next;
        if(!judge)RoundInput.Text=next.Label;
        RenderRoster(next);Message.Text=Message.Text.StartsWith("Подключаемся")?"Связь установлена. Подтвердите готовность перед стартом.":Message.Text;
    }
    void RenderRoster(Snapshot next)
    {
        Roster.Children.Clear();PilotCount.Text=$"{next.Peers.Length} участников";
        foreach(var p in next.Peers)
        {
            bool active=next.StartAt.HasValue&&next.ServerMs<next.StartAt.Value+3000;
            string badge=p.AgeMs>3000?"НЕТ СВЯЗИ":active?(p.Received?"СИГНАЛ ПОЛУЧЕН":"ДОСТАВКА…"):p.Ready?"ГОТОВ К СТАРТУ":"ОЖИДАНИЕ";
            var row=new Grid{Margin=new(0,0,0,10)};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var avatar=new Border{Width=34,Height=34,CornerRadius=new(10),Background=Paint.Brush("#243246"),Margin=new(0,0,12,0),Child=new TextBlock{Text=p.Name.Length>0?p.Name[..1].ToUpperInvariant():"P",HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=Paint.Brush("#A9CBD5"),FontWeight=FontWeights.SemiBold}};row.Children.Add(avatar);
            var name=new TextBlock{Text=p.Name+(p.Username==null?"":"  @"+p.Username),ToolTip=p.UserId==null?null:"Discord ID: "+p.UserId,VerticalAlignment=VerticalAlignment.Center,FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(name,1);row.Children.Add(name);
            var tag=new TextBlock{Text=badge,VerticalAlignment=VerticalAlignment.Center,FontSize=10,Foreground=Paint.Brush(p.AgeMs>3000?"#FF6879":p.Ready||active&&p.Received?"#79E8B4":"#7E93AA")};Grid.SetColumn(tag,2);row.Children.Add(tag);Roster.Children.Add(row);
        }
        if(next.Peers.Length==0)Roster.Children.Add(new TextBlock{Text="Пригласите пилотов в комнату",Foreground=Paint.Brush("#7E93AA"),Margin=new(0,15,0,0)});
    }
    async void Ready_Changed(object sender,RoutedEventArgs e){if(suppressReady||relay==null)return;await relay.Ready(ReadyCheck.IsChecked==true);}
    async void Start_Click(object sender,RoutedEventArgs e){if(relay!=null)await relay.Control("start",RoundInput.Text,Delay10.IsChecked==true?10000:6000);}
    async void Cancel_Click(object sender,RoutedEventArgs e){if(relay!=null)await relay.Control("cancel",RoundInput.Text);}
    async void Prune_Click(object sender,RoutedEventArgs e){if(relay!=null)await relay.Control("prune",RoundInput.Text);}
    void Tick()
    {
        if(demo)return;
        bool online=relay?.Connected==true;var clock=relay?.Clock??new(false,0,0);bool good=online&&clock.Synced&&state!=null;
        bool active=state?.StartAt is double at&&RelayClient.Now+clock.Offset<at+3000;
        StartButton.IsEnabled=judge&&good&&!active&&state!.Peers.Length>0&&state.Peers.All(p=>p.Ready&&p.AgeMs<=3000&&p.Received);
        CancelButton.IsEnabled=judge&&online;ReadyCheck.IsEnabled=!judge&&good&&!active;CopyInviteButton.IsEnabled=RoomInput.Text.Trim().Length>=6;
        PruneButton.IsEnabled=judge&&online&&!active;
        if(relay==null)return;
        if(!good&&!active&&ReadyCheck.IsChecked==true){SetReady(false);_ = relay.Ready(false);}
        ConnectionStatus.Text=online?(judge?"Судья в комнате":"Пилот в комнате"):"Переподключение";ConnectionDot.Fill=Paint.Brush(online?"#79E8B4":"#FFCA69");
        SyncLabel.Text=clock.Synced?$"Синхронизация · RTT {clock.Rtt:F0} мс":"Синхронизация часов…";
        string text,hint;int light=-1;
        if(!online){text="Нет связи";hint="Не стартуйте. Ждём соединение.";if(ReadyCheck.IsChecked==true)SetReady(false);}
        else if(!good){text="Сверка";hint="Синхронизируем часы с сервером";}
        else if(state!.StartAt==null){text="Готовы?";hint="Ожидаем команду судьи";}
        else if(!judge&&!state.Seated){text="Наблюдение";hint="Подключение после назначения старта";}
        else
        {
            double remaining=state.StartAt!.Value-(RelayClient.Now+clock.Offset);
            if(remaining>2000){text=Math.Ceiling(remaining/1000).ToString("00");hint="Приготовиться к старту";light=0;}
            else if(remaining>0){text=Math.Ceiling(remaining/1000).ToString("00");hint="Внимание. Следите за сигналом.";light=1;}
            else if(remaining> -3000){text="СТАРТ";hint="Поехали. Чистого заезда!";light=2;}
            else{text="В заезде";hint="Для следующего старта отметьте готовность";}
        }
        SignalText.Text=text;SignalText.FontSize=text.Length>9?30:text.Length>5?40:60;SignalHint.Text=hint;
        string[] colors=["#FF6879","#FFCA69","#79E8B4"];SignalText.Foreground=Paint.Brush(light>=0?colors[light]:"#B4C6D9");
        var lamps=new[]{RedLamp,AmberLamp,GreenLamp};for(int i=0;i<3;i++)lamps[i].Fill=Paint.Brush(i==light?colors[i]:"#2C3746");
        hud?.Display(text,state?.Label??"DRIFTSTART / LIVE SESSION",light,hint.ToUpperInvariant());
    }
    void CopyInvite_Click(object sender,RoutedEventArgs e)
    {
        var json=JsonSerializer.Serialize(new{server=UrlInput.Text.Trim(),room=RoomInput.Text.Trim()});
        try{Clipboard.SetText("driftstart:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+','-').Replace('/','_'));Message.Text="Приглашение скопировано. В нём только адрес и код комнаты; ключ судьи не включён.";}catch(System.Runtime.InteropServices.COMException){Message.Text="Буфер обмена занят. Повторите копирование.";}
    }
    void PasteInvite_Click(object sender,RoutedEventArgs e)
    {
        if(relay!=null||loginStop!=null){Message.Text="Сначала отключитесь от текущей комнаты.";return;}
        try{string text=Clipboard.GetText().Trim();if(!text.StartsWith("driftstart:"))throw new FormatException();text=text[11..].Replace('-','+').Replace('_','/');text=text.PadRight((text.Length+3)/4*4,'=');using var doc=JsonDocument.Parse(Convert.FromBase64String(text));UrlInput.Text=doc.RootElement.GetProperty("server").GetString();RoomInput.Text=doc.RootElement.GetProperty("room").GetString();Message.Text="Приглашение заполнено. Укажите имя и подключитесь.";}catch(Exception ex)when(ex is FormatException or JsonException or KeyNotFoundException or System.Runtime.InteropServices.COMException){Message.Text="В буфере обмена нет корректного приглашения DriftStart.";}
    }
    void ImportProfile_Click(object sender,RoutedEventArgs e)
    {
        if(relay!=null||loginStop!=null){Message.Text="Сначала отключитесь от комнаты.";return;}
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Открыть файл доступа DriftStart",Filter="Подключение DriftStart (*.json)|*.json",CheckFileExists=true};
        if(dialog.ShowDialog(this)==true)ImportProfile(dialog.FileName);
    }
    public void ImportProfile(string path)
    {
        try{using var doc=JsonDocument.Parse(File.ReadAllText(path));var p=doc.RootElement;UrlInput.Text=p.GetProperty("server").GetString();RoomInput.Text=p.GetProperty("room").GetString();if(p.TryGetProperty("name",out var name))NameInput.Text=name.GetString();bool isJudge=p.TryGetProperty("judgeKey",out var key)&&!string.IsNullOrEmpty(key.GetString());RoleJudge.IsChecked=isJudge;RolePlayer.IsChecked=!isJudge;JudgeKeyInput.Password=isJudge?key.GetString()!:"";JudgeKeyPanel.Visibility=Visibility.Collapsed;Message.Text="Доступ загружен. Войдите через Discord; этот аккаунт будет привязан как владелец, если владелец ещё не назначен.";}catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException){Message.Text="Не удалось прочитать файл подключения DriftStart.";}
    }
    void ApplyHudSettings(){if(hud==null)return;hud.Configure(HudScale.Value,HudOpacity.Value,MoveHudCheck.IsChecked==true);if(ShowHudCheck.IsChecked==true)hud.Show();else hud.Hide();}
    void HudSettings_Changed(object sender,RoutedEventArgs e){if(IsInitialized)ApplyHudSettings();}
    void HudSlider_Changed(object sender,RoutedPropertyChangedEventArgs<double> e){if(IsInitialized)ApplyHudSettings();}
    void ResetHud_Click(object sender,RoutedEventArgs e)=>hud?.Center();
    public void Demo()
    {
        demo=true;judge=true;ShowPage("race");ConnectionStatus.Text="Судья в комнате";ConnectionDot.Fill=Paint.Brush("#79E8B4");
        SignalText.Text="03";SignalText.FontSize=66;SignalText.Foreground=Paint.Brush("#FF6879");SignalHint.Text="Приготовиться к старту";RedLamp.Fill=Paint.Brush("#FF6879");SyncLabel.Text="Синхронизация · RTT 34 мс";RoundInput.Text="Пара 12 / проезд 1";StartButton.IsEnabled=false;CancelButton.IsEnabled=true;
        RenderRoster(new(1000,"demo",1,1,"Пара 12 / проезд 1",4000,true,[new("1","EGZOT",false,0,true),new("2","SMOKE",false,0,true),new("3","NIGHTRUNNER",false,0,true)]));
        Message.Text="Все пилоты получили команду. Старт назначен на единый момент времени. Демонстрационные данные.";
    }
}
