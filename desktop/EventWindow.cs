using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DriftStart;
public record EventSummary(string Id,string Name,string Phase,int Count,string? Champion){public override string ToString()=>$"{Name}  ·  {EventWindow.PhaseName(Phase)}  ·  {Count} пилотов";}
public record Entry(string Id,string Name,double?[] Scores,int Rank,double? Best,double? Second,string? StaticId=null,string? VehicleName=null,string? VehicleImage=null,bool Manual=false,Dictionary<string,double>[]? DisciplineScores=null)
{public override string ToString()=>Name+(StaticId==null?"":"  #"+StaticId)+(VehicleName==null?"":"  ·  "+VehicleName);}
public record Match(string Id,string? A,string? B,string? Winner,bool Done,bool Bye,Dictionary<string,double>[]? TandemScores=null,Dictionary<string,Dictionary<string,double>>[]? TandemDisciplineScores=null);
public record Audit(double At,string Actor,string Name,string Action,string Detail,string? Reason);
public record Placements(string? First,string? Second,string? Third,string? QualificationLeader);
public record DisciplineChoice(string Id,string Name){public override string ToString()=>Name;}
public record Tournament(string Id,string Name,string Phase,int Revision,Entry[] Entries,Entry[]? Standings,Match[][] Rounds,string? Champion,Audit[] Audit,Match? ThirdPlace=null,Placements? Placements=null);
public sealed class EventWindow:Window
{
    public event Action<string>? StartRequested;
    public event Action<Entry,string?>? QualificationHudRequested;
    public event Action<Entry,string?>? QualificationHudUpdated;
    public event Action<Entry,Entry,Match,string?>? PairHudRequested;
    public event Action<Entry,Entry,Match,string?>? PairHudUpdated;
    public event Action? FullscreenRequested;
    readonly CloudApi api;readonly ComboBox picker=new(){MinWidth=360,MaxWidth=560,FontSize=13,Padding=new(8),Foreground=Paint.Brush("#ECF2F7")};
    readonly StackPanel content=new();readonly ScrollViewer scroll=new();readonly TextBlock status=Text("Загрузка…",12,"#85858A");readonly TextBlock identity=Text("",12,"#F20D4F");
    readonly Dictionary<string,Button> tabs=[];readonly Button create;
    Tournament? current;Account[] users=[];string page="qualification",selected="",activeQualification="",activePair="",leaderDiscipline="trajectory";bool busy,queued,selecting,dialogOpen,closed;Window? dialogOwner;
    bool CanJudge=>api.Me?.Role is "judge" or "leader" or "cohost";
    string? CurrentDiscipline=>users.FirstOrDefault(x=>x.Id==api.Me?.Id)?.Discipline;
    string? HudDiscipline=>api.Me?.Role=="leader"?leaderDiscipline:CurrentDiscipline;
    public static string PhaseName(string phase)=>phase switch{"qualification"=>"Квалификация","bracket"=>"Парные заезды","completed"=>"Завершён","archived"=>"Архив",_=>phase};
    public EventWindow(CloudApi api)
    {
        this.api=api;Title="DriftStart — Турниры и судьи";Width=1180;Height=850;MinWidth=1000;MinHeight=720;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=Paint.Brush("#0B0810");
        var root=new Grid{Margin=new(0)};root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var head=new DockPanel{Margin=new(0,0,0,8)};head.Children.Add(Text("УПРАВЛЕНИЕ СОРЕВНОВАНИЕМ",10,"#777D8D"));identity.HorizontalAlignment=HorizontalAlignment.Right;identity.VerticalAlignment=VerticalAlignment.Center;head.Children.Add(identity);root.Children.Add(head);
        var toolbar=new StackPanel();Grid.SetRow(toolbar,1);root.Children.Add(toolbar);
        var row=new WrapPanel{Margin=new(0,0,0,10)};picker.SelectionChanged+=async(_,_)=>{if(!selecting&&picker.SelectedItem is EventSummary t){selected=t.Id;await Refresh();}};row.Children.Add(picker);
        create=Button("+ Новый турнир",async()=>{var name=Ask("Новый турнир","Название турнира","");if(name!=null)await Run(async()=>{var r=await api.Request("/api/tournaments",new{name});selected=r.GetProperty("event").GetProperty("id").GetString()!;});},true);create.Margin=new(10,0,0,0);row.Children.Add(create);
        var refresh=Button("Обновить",async()=>await Refresh());refresh.Margin=new(10,0,0,0);row.Children.Add(refresh);toolbar.Children.Add(row);
        var nav=new WrapPanel{Margin=new(0,0,0,12)};foreach(var (key,title)in new[]{("qualification","01  Квалификация"),("bracket","02  Парная сетка"),("history","03  История"),("judges","04  Участники и роли")}){var k=key;var b=Button(title,()=>{page=k;Render();return Task.CompletedTask;});b.Margin=new(0,0,7,0);tabs[k]=b;nav.Children.Add(b);}toolbar.Children.Add(nav);
        scroll.Content=content;scroll.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;scroll.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;Grid.SetRow(scroll,2);root.Children.Add(scroll);Grid.SetRow(status,3);status.FontSize=10;status.Margin=new(0,8,0,0);root.Children.Add(status);
        Loaded+=async(_,_)=>{if(!App.Preview)await Refresh();};Closed+=(_,_)=>closed=true;
    }
    public UIElement TakeContent(Window owner){dialogOwner=owner;var view=(UIElement)Content;Content=null;return view;}
    public Task LoadEmbedded()=>Refresh();
    public void DisposeEmbedded(){closed=true;}
    static TextBlock Text(string s,double size=14,string color="#ECF2F7")=>new(){Text=s,FontSize=size,Foreground=Paint.Brush(color),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    Button Button(string label,Func<Task> action,bool primary=false){var b=new Button{Content=Text(label,12,"#ECF2F7"),Padding=new(12,8,12,8),Style=(Style)Application.Current.FindResource(primary?"Primary":typeof(Button))};b.Click+=async(_,_)=>{b.IsEnabled=false;try{await action();}catch(Exception ex){status.Text=ex.Message;}finally{b.IsEnabled=true;}};return b;}
    Border Card(UIElement child)=>new(){Child=child,Background=Paint.Brush("#13151C"),BorderBrush=Paint.Brush("#2A2D38"),BorderThickness=new(1),CornerRadius=new(8),Padding=new(14),Margin=new(0,0,0,12)};
    public void RefreshFromSignal(){if(dialogOpen){queued=true;status.Text="Есть обновления. Они загрузятся после закрытия окна ввода.";}else _=Refresh();}
    async Task Refresh()
    {
        if(closed||App.Preview)return;if(busy){queued=true;return;}busy=true;picker.IsEnabled=false;
        try{
            var accountResponse=await api.Request("/api/members");users=accountResponse.GetProperty("members").Deserialize<Account[]>(CloudApi.JsonOptions)??[];
            var self=users.FirstOrDefault(x=>x.Id==api.Me!.Id);if(self!=null){api.SetRoomRole(self.Role);identity.Text=$"{self.Name} · {self.RoleLabel}";}else identity.Text=$"{api.Me!.Name} · {api.Me.RoleLabel}";create.IsEnabled=CanJudge;
            var list=await api.Request("/api/tournaments");var events=list.GetProperty("events").Deserialize<EventSummary[]>(CloudApi.JsonOptions)??[];
            if(!events.Any(t=>t.Id==selected))selected=events.FirstOrDefault()?.Id??"";
            selecting=true;picker.ItemsSource=events;picker.SelectedItem=events.FirstOrDefault(t=>t.Id==selected);selecting=false;
            current=selected.Length>0?(await api.Request("/api/tournament?id="+selected)).GetProperty("event").Deserialize<Tournament>(CloudApi.JsonOptions):null;
            if(activeQualification.Length>0&&current?.Entries.FirstOrDefault(x=>x.Id==activeQualification)is Entry active)QualificationHudUpdated?.Invoke(active,HudDiscipline);
            if(activePair.Length>0&&current is Tournament ct&&FindMatch(ct,activePair) is Match pm&&pm.A!=null&&pm.B!=null)PairHudUpdated?.Invoke(ct.Entries.First(x=>x.Id==pm.A),ct.Entries.First(x=>x.Id==pm.B),pm,HudDiscipline);
            Render();status.Text="●  Изменения сохранены";
        }catch(Exception ex){status.Text=ex.Message;}finally{busy=false;picker.IsEnabled=true;if(queued&&!dialogOpen){queued=false;_ = Dispatcher.BeginInvoke(()=>_=Refresh());}}
    }
    async Task Run(Func<Task> operation){if(busy){status.Text="Дождитесь загрузки и повторите действие.";return;}busy=true;string? error=null;try{await operation();}catch(Exception ex){error=ex.Message;}busy=false;await Refresh();if(error!=null)status.Text=error;}
    async Task Command(object value)=>await Run(async()=>await api.Request("/api/tournament",value));
    public async Task ScoreFromHud(Entry pilot,int attempt,double score,string? discipline){if(current==null)return;string? selected=api.Me?.Role=="leader"?(discipline??leaderDiscipline):discipline;await Command(new{id=current.Id,revision=current.Revision,action="score",userId=pilot.Id,attempt,score,discipline=selected,reason="Оценка из судейского HUD"});}
    public void NavigateQualification(int direction){if(current==null||current.Entries.Length==0)return;var ordered=current.Standings??current.Entries;int index=Array.FindIndex(ordered,x=>x.Id==activeQualification);index=index<0?0:(index+direction+ordered.Length)%ordered.Length;activeQualification=ordered[index].Id;QualificationHudRequested?.Invoke(ordered[index],HudDiscipline);}
    public async Task ScorePairFromHud(string matchId,Entry pilot,int run,double score,string? discipline){if(current==null)return;string? selected=api.Me?.Role=="leader"?(discipline??leaderDiscipline):discipline;await Command(new{id=current.Id,revision=current.Revision,action="tandemScore",matchId,userId=pilot.Id,run,score,discipline=selected,reason="Оценка парного заезда из судейского HUD"});}
    static Match? FindMatch(Tournament t,string id)=>id=="third"?t.ThirdPlace:t.Rounds.SelectMany(x=>x).FirstOrDefault(x=>x.Id==id);
    string? Ask(string title,string label,string initial,bool reason=false)
    {
        dialogOpen=true;var d=new Window{Title=title,Owner=dialogOwner??this,Width=500,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Paint.Brush("#141C27")};
        var panel=new StackPanel{Margin=new(25)};panel.Children.Add(Text(label,16));var input=new TextBox{Text=initial,Margin=new(0,16,0,20),MaxLength=reason?200:64};panel.Children.Add(input);
        var ok=new Button{Content="Сохранить",IsDefault=true,Style=(Style)Application.Current.FindResource("Primary")};ok.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(input.Text))d.DialogResult=true;};panel.Children.Add(ok);d.Content=panel;d.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};var result=d.ShowDialog()==true?input.Text.Trim():null;dialogOpen=false;if(queued){queued=false;_ = Dispatcher.BeginInvoke(()=>_=Refresh());}return result;
    }
    Dictionary<string,double>? AskAttemptScores(Entry pilot,int attempt,string? assigned)
    {
        dialogOpen=true;var d=new Window{Title=$"{pilot.Name} · попытка {attempt+1}",Owner=dialogOwner??this,Width=620,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Paint.Brush("#0A0A0C")};
        var root=new StackPanel{Margin=new(26)};root.Children.Add(Text($"{pilot.Name}  ·  ПОПЫТКА {attempt+1}",20));root.Children.Add(Text("Три оценки образуют итог из 100 баллов",11,"#85858A"));
        var row=new Grid{Margin=new(0,20,0,12)};for(int i=0;i<3;i++)row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});var fields=new Dictionary<string,(TextBox Box,double Max)>();var definitions=new[]{("trajectory","ТРАЕКТОРИЯ",40d),("angle","УГОЛ",35d),("style","СТИЛЬ",25d)};
        for(int i=0;i<definitions.Length;i++){var item=definitions[i];var panel=new StackPanel{Margin=new(i==0?0:7,0,i==2?0:7,0)};panel.Children.Add(Text($"{item.Item2} · 0–{item.Item3:0}",10,"#F20D4F"));var old=pilot.DisciplineScores?.ElementAtOrDefault(attempt)?.GetValueOrDefault(item.Item1);var input=new TextBox{Text=(old??0).ToString(CultureInfo.InvariantCulture),FontSize=20,FontWeight=FontWeights.Bold,Margin=new(0,7,0,0),IsEnabled=api.Me?.Role=="leader"||assigned==item.Item1};fields[item.Item1]=(input,item.Item3);panel.Children.Add(input);Grid.SetColumn(panel,i);row.Children.Add(panel);}root.Children.Add(row);
        var total=Text("ИТОГ: 0 / 100",18,"#79E8B4");root.Children.Add(total);void UpdateTotal(){double sum=0;foreach(var item in fields.Values)if(double.TryParse(item.Box.Text.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))sum+=value;total.Text=$"ИТОГ: {sum:0.##} / 100";}foreach(var item in fields.Values)item.Box.TextChanged+=(_,_)=>UpdateTotal();UpdateTotal();
        var save=Button("СОХРАНИТЬ ОЦЕНКИ",()=>Task.CompletedTask,true);save.Margin=new(0,20,0,0);save.Click+=(_,_)=>{foreach(var item in fields){if(!double.TryParse(item.Value.Box.Text.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||value<0||value>item.Value.Max){status.Text=$"{Account.DisciplineLabel(item.Key)}: допустимо 0–{item.Value.Max:0}.";return;}}d.DialogResult=true;};root.Children.Add(save);d.Content=root;
        var accepted=d.ShowDialog()==true;dialogOpen=false;if(queued){queued=false;_ = Dispatcher.BeginInvoke(()=>_=Refresh());}if(!accepted)return null;var result=new Dictionary<string,double>();foreach(var item in fields)if(api.Me?.Role=="leader"||assigned==item.Key)result[item.Key]=double.Parse(item.Value.Box.Text.Replace(',','.'),CultureInfo.InvariantCulture);return result;
    }
    void Render()
    {
        scroll.HorizontalScrollBarVisibility=page is "bracket" or "qualification"?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled;
        content.Children.Clear();foreach(var(k,b)in tabs){b.Background=Paint.Brush(k==page?"#35202E":"#151821");b.BorderBrush=Paint.Brush(k==page?"#F20D4F":"#2A2D38");}
        if(page=="judges"){RenderJudges();return;}
        if(current==null){content.Children.Add(Card(Text("Турниров пока нет. Судья или владелец может создать первый турнир.",18)));return;}
        var t=current;var intro=new StackPanel();var overview=new DockPanel();var titleBlock=new StackPanel{Margin=new(0,0,18,0)};titleBlock.Children.Add(Text(t.Name,19));titleBlock.Children.Add(Text($"{PhaseName(t.Phase)} · {t.Entries.Length} пилотов",11,"#858996"));if(CanJudge){var removeTournament=Button("Удалить турнир",async()=>{if(MessageBox.Show(dialogOwner??this,$"Удалить турнир «{t.Name}» вместе с историей?","Удаление турнира",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes){await api.Request("/api/tournament",new{id=t.Id,action="delete"});selected="";await Refresh();}});removeTournament.HorizontalAlignment=HorizontalAlignment.Right;removeTournament.FontSize=10;removeTournament.Background=Brushes.Transparent;removeTournament.Foreground=Paint.Brush("#AA7185");DockPanel.SetDock(removeTournament,Dock.Right);overview.Children.Add(removeTournament);}overview.Children.Add(titleBlock);intro.Children.Add(overview);if(t.Placements is Placements places)intro.Children.Add(PlacementsView(places));else if(t.Champion!=null)intro.Children.Add(Text("Победитель: "+PilotName(t.Champion),20,"#79E8B4"));content.Children.Add(Card(intro));
        if(page=="qualification")RenderQualification(t);else if(page=="bracket")RenderBracket(t);else RenderHistory(t);
    }
    string PilotName(string? id)=>id==null?"Ожидает соперника":current?.Entries.FirstOrDefault(p=>p.Id==id)?.Name??id;
    UIElement PlacementsView(Placements places){var grid=new Grid{Margin=new(0,18,0,2)};for(int i=0;i<4;i++)grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});var items=new[]{("01","ЧЕМПИОН",PilotName(places.First),"#F20D4F"),("02","ВТОРОЕ МЕСТО",PilotName(places.Second),"#C8CDD5"),("03","ТРЕТЬЕ МЕСТО",PilotName(places.Third),"#D49A62"),("Q","ЛИДЕР КВАЛЫ",PilotName(places.QualificationLeader),"#79E8B4")};for(int i=0;i<items.Length;i++){var item=items[i];var panel=new StackPanel();panel.Children.Add(Text(item.Item1,22,item.Item4));panel.Children.Add(Text(item.Item2,9,"#77777C"));var pilot=Text(item.Item3,15,"#FFFFFF");pilot.FontWeight=FontWeights.SemiBold;pilot.Margin=new(0,7,0,0);panel.Children.Add(pilot);var card=new Border{Child=panel,Background=Paint.Brush("#151518"),BorderBrush=Paint.Brush(item.Item4),BorderThickness=new(2,0,0,0),Padding=new(14),Margin=new(i==0?0:6,0,i==3?0:6,0)};Grid.SetColumn(card,i);grid.Children.Add(card);}return grid;}
    static string AttemptText(Entry p,int attempt){var d=p.DisciplineScores?.ElementAtOrDefault(attempt);string V(string key)=>d?.GetValueOrDefault(key).ToString("0.##")??"—";return $"Σ {p.Scores.ElementAtOrDefault(attempt)?.ToString("0.##")??"—"}\nТ {V("trajectory")}  ·  У {V("angle")}  ·  С {V("style")}";}
    void RenderQualification(Tournament t)
    {
        var judgingTools=new WrapPanel{Margin=new(0,0,0,10)};content.Children.Add(judgingTools);
        if(api.Me?.Role=="leader"){var disciplineRow=new WrapPanel{Margin=new(0,0,16,6)};disciplineRow.Children.Add(Text("ЛИДЕР ОЦЕНИВАЕТ: ",12,"#F20D4F"));var disciplinePicker=new ComboBox{ItemsSource=new[]{new DisciplineChoice("trajectory","Траектория"),new DisciplineChoice("angle","Угол"),new DisciplineChoice("style","Стиль")},MinWidth=150,Padding=new(8)};disciplinePicker.SelectedItem=((DisciplineChoice[])disciplinePicker.ItemsSource).First(x=>x.Id==leaderDiscipline);disciplinePicker.SelectionChanged+=(_,_)=>{if(disciplinePicker.SelectedItem is DisciplineChoice d&&d.Id!=leaderDiscipline){leaderDiscipline=d.Id;Render();}};disciplineRow.Children.Add(disciplinePicker);judgingTools.Children.Add(disciplineRow);}
        if(CanJudge&&t.Entries.Length>0){var hudRow=new WrapPanel{Margin=new(0,0,0,6)};var hudPilot=new ComboBox{ItemsSource=t.Entries,MinWidth=220,MaxWidth=300,Padding=new(8)};hudPilot.SelectedIndex=0;hudRow.Children.Add(hudPilot);var show=Button("СУДЕЙСКИЙ HUD",()=>{if(hudPilot.SelectedItem is Entry p){activeQualification=p.Id;QualificationHudRequested?.Invoke(p,HudDiscipline);}return Task.CompletedTask;},true);show.Margin=new(10,0,0,0);hudRow.Children.Add(show);judgingTools.Children.Add(hudRow);}
        if(t.Phase=="qualification"){
            var row=new WrapPanel{Margin=new(0,0,0,10)};
            if(CanJudge){var choice=new ComboBox{ItemsSource=users.Where(u=>!t.Entries.Any(e=>e.Id==u.Id)&&u.Role=="pilot").ToArray(),MinWidth=240,MaxWidth=430,Foreground=Paint.Brush("#ECF2F7"),Padding=new(8),Margin=new(0,0,10,0)};row.Children.Add(choice);row.Children.Add(Button("Добавить из группы",async()=>{if(choice.SelectedItem is Account a)await Command(new{id=t.Id,revision=t.Revision,action="join",userId=a.Id});}));var manual=Button("Вписать вручную",async()=>{string? name=Ask("Ручной участник","Игровой ник","");string? staticId=name==null?null:Ask("Ручной участник","Статик","");string? car=staticId==null?null:Ask("Ручной участник","Автомобиль"," ");if(car!=null)await Command(new{id=t.Id,revision=t.Revision,action="manualJoin",name,staticId,vehicleName=car});});manual.Margin=new(10,0,0,0);row.Children.Add(manual);}content.Children.Add(row);
        }
        var table=new StackPanel{MinWidth=810};var heading=QualRow("#","Пилот / статик","Попытка 1","Попытка 2","Лучший","");heading.Background=Paint.Brush("#1C202B");table.Children.Add(heading);
        foreach(var p in t.Standings??t.Entries){var row=QualRow(p.Rank.ToString("00"),p.Name+(p.StaticId==null?"":"  #"+p.StaticId)+"\n"+(p.VehicleName??"Автомобиль не указан"),AttemptText(p,0),AttemptText(p,1),p.Best?.ToString("0.##")??"—");row.Background=Paint.Brush(p.Id==activeQualification?"#2B1B2A":p.Rank%2==0?"#171A23":"#13151C");
            string? scoreDiscipline=api.Me?.Role=="leader"?leaderDiscipline:CurrentDiscipline;if((api.Me?.Role=="leader"||api.Me?.Role=="judge")&&scoreDiscipline!=null&&t.Phase=="qualification")for(int i=0;i<2;i++){int attempt=i;var b=Button(AttemptText(p,attempt),async()=>{var scores=AskAttemptScores(p,attempt,scoreDiscipline);if(scores!=null)await Command(new{id=t.Id,revision=t.Revision,action="scoreAttempt",userId=p.Id,attempt,scores});});Grid.SetColumn(b,i+2);row.Children.RemoveAt(i+2);row.Children.Insert(i+2,b);}if(CanJudge&&t.Phase=="qualification"){var remove=Button("Удалить",async()=>{if(MessageBox.Show(dialogOwner??this,$"Удалить {p.Name} из турнира?","Состав турнира",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)await Command(new{id=t.Id,revision=t.Revision,action="removeEntry",userId=p.Id});});Grid.SetColumn(remove,5);row.Children.Add(remove);}
            table.Children.Add(row);
        }content.Children.Add(Card(table));
        if(CanJudge&&t.Phase=="qualification"){
            var bar=new WrapPanel();bar.Children.Add(Button("Сформировать парную сетку автоматически",async()=>{if(MessageBox.Show(dialogOwner??this,"В сетку попадут все пилоты с лучшим результатом больше 1 балла. Первый будет поставлен против последнего. Продолжить?","Сформировать сетку",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)await Command(new{id=t.Id,revision=t.Revision,action="bracket"});},true));content.Children.Add(bar);
        }
    }
    Grid QualRow(params string[] values){var row=new Grid{Margin=new(0,2,0,2),MinHeight=44};foreach(var width in new[]{40d,270,140,140,85,95})row.ColumnDefinitions.Add(new(){Width=new(width)});row.ColumnDefinitions[1].Width=new(1,GridUnitType.Star);row.ColumnDefinitions[1].MinWidth=220;for(int i=0;i<values.Length;i++){var text=Text(values[i],i==1?12:13,i==4?"#79E8B4":"#B7C7D9");text.Margin=new(8,5,8,5);Grid.SetColumn(text,i);row.Children.Add(text);}return row;}
    void RenderBracket(Tournament t)
    {
        if(t.Rounds.Length==0){content.Children.Add(Card(Text("Сетка появится после квалификации. В неё автоматически попадут все пилоты с результатом больше 1 балла.")));return;}
        var shell=new Border{Background=Paint.Brush("#0A0A0C"),BorderBrush=Paint.Brush("#303034"),BorderThickness=new(1),Padding=new(14),Margin=new(0,14,0,0)};
        var panel=new StackPanel();shell.Child=panel;
        var title=new Border{Background=Paint.Brush("#19191C"),BorderBrush=Paint.Brush("#F20D4F"),BorderThickness=new(3,0,0,0),Padding=new(18,7,18,7),HorizontalAlignment=HorizontalAlignment.Stretch};
        title.Child=Text($"DRIFTSTART  /  {t.Name.ToUpperInvariant()}  —  TANDEM RUNS STANDINGS",15,"#FFFFFF");panel.Children.Add(title);
        var bracketTools=new DockPanel();bracketTools.Children.Add(Text("Нажмите на пару для управления. Пилотов первого раунда можно перетаскивать за ник.",11,"#85858A"));var fullscreen=Button("НА ВЕСЬ ЭКРАН",()=>{FullscreenRequested?.Invoke();return Task.CompletedTask;});fullscreen.Margin=new(0,0,8,0);DockPanel.SetDock(fullscreen,Dock.Right);bracketTools.Children.Insert(0,fullscreen);panel.Children.Add(bracketTools);
        if(CanJudge&&FindMatch(t,activePair)is Match selectedPair&&selectedPair.A!=null&&selectedPair.B!=null){var a=t.Entries.First(x=>x.Id==selectedPair.A);var b=t.Entries.First(x=>x.Id==selectedPair.B);var pairTools=new WrapPanel{Margin=new(0,10,0,0)};pairTools.Children.Add(Text($"ВЫБРАНА ПАРА:  1  {a.Name}   /   2  {b.Name}",12,"#F20D4F"));var start=Button("НА СТАРТ",()=>{StartRequested?.Invoke($"{a.Name} #{a.StaticId??"—"}  /  {b.Name} #{b.StaticId??"—"}");return Task.CompletedTask;},true);start.Margin=new(14,0,0,0);pairTools.Children.Add(start);var judgeHud=Button("СУДЕЙСКИЙ HUD",()=>{PairHudRequested?.Invoke(a,b,selectedPair,HudDiscipline);return Task.CompletedTask;});judgeHud.Margin=new(8,0,0,0);pairTools.Children.Add(judgeHud);if(selectedPair.Done&&!selectedPair.Bye&&t.Phase!="archived"){var clear=Button("ОТМЕНИТЬ РЕЗУЛЬТАТ",async()=>await Command(new{id=t.Id,revision=t.Revision,action="clearWinner",matchId=selectedPair.Id,reason=""}));clear.Margin=new(8,0,0,0);pairTools.Children.Add(clear);}panel.Children.Add(pairTools);}
        var arena=new Grid{Margin=new(0,18,0,4),LayoutTransform=new ScaleTransform(t.Rounds.Length>4?.72:t.Rounds.Length>3?.84:1,1)};arena.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});arena.ColumnDefinitions.Add(new(){Width=new(225)});arena.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        var left=BracketSide(t,true);arena.Children.Add(left);
        var center=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new(14,0,14,0)};Grid.SetColumn(center,1);
        center.Children.Add(Text("GRAND FINAL",12,"#F20D4F"));center.Children.Add(MatchCard(t,t.Rounds[^1][0],t.Rounds.Length-1));
        if(t.ThirdPlace is Match third){center.Children.Add(Text("THIRD PLACE RUN",11,"#F20D4F"));center.Children.Add(MatchCard(t,third,t.Rounds.Length));}if(t.Champion!=null){var champ=new Border{Background=Paint.Brush("#F20D4F"),Padding=new(12,8,12,8),Margin=new(0,10,0,0)};champ.Child=Text("CHAMPION  ·  "+PilotName(t.Champion).ToUpperInvariant(),13,"#FFFFFF");center.Children.Add(champ);}arena.Children.Add(center);
        var right=BracketSide(t,false);Grid.SetColumn(right,2);arena.Children.Add(right);panel.Children.Add(arena);content.Children.Add(shell);
    }
    UIElement BracketSide(Tournament t,bool left)
    {
        var side=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=left?HorizontalAlignment.Right:HorizontalAlignment.Left};
        var indices=Enumerable.Range(0,Math.Max(0,t.Rounds.Length-1));if(!left)indices=indices.Reverse();
        foreach(int r in indices){var matches=t.Rounds[r];int half=(matches.Length+1)/2;var chosen=left?matches.Take(half):matches.Skip(half);var col=new StackPanel{Width=215,VerticalAlignment=VerticalAlignment.Center,Margin=new(5,0,5,0)};col.Children.Add(Text(r==t.Rounds.Length-2?"SEMI FINAL":$"TOP {matches.Length*2}",11,"#666666"));double gap=Math.Min(70,8+r*14);foreach(var m in chosen){var card=MatchCard(t,m,r);card.Margin=new(0,gap,0,gap);col.Children.Add(card);}side.Children.Add(col);}return side;
    }
    Border MatchCard(Tournament t,Match m,int round)
    {
        var box=new StackPanel();box.Children.Add(Text(m.Id=="third"?"BRONZE RUN":$"RUN {round+1}.{int.Parse(m.Id.Split('-')[1])+1}",9,"#85858A"));
        foreach(var item in new[]{(Slot:"a",Id:m.A),(Slot:"b",Id:m.B)})
        {
            string? id=item.Id;bool winner=id==m.Winner&&m.Done;
            var row=new DockPanel{Background=Paint.Brush(winner?"#F20D4F":"#252529"),Margin=new(0,2,0,0),AllowDrop=CanJudge&&round==0};
            var entry=current?.Entries.FirstOrDefault(x=>x.Id==id);
            string title=(winner?"✓  ":"")+(id==null?(m.Bye?"АВТОМАТИЧЕСКИЙ ПРОХОД":"СВОБОДНЫЙ СЛОТ"):PilotName(id))+(entry?.StaticId==null?"":"  #"+entry.StaticId);
            var position=Text(item.Slot=="a"?"1":"2",11,"#F20D4F");position.FontWeight=FontWeights.Bold;position.Margin=new(8,5,2,5);DockPanel.SetDock(position,Dock.Left);row.Children.Add(position);var name=Text(title,12,id==null?"#77777C":"#FFFFFF");name.Margin=new(7,5,7,5);
            if(CanJudge&&round==0&&id!=null){string dragId=id;name.Cursor=System.Windows.Input.Cursors.Hand;name.MouseMove+=(_,e)=>{if(e.LeftButton==System.Windows.Input.MouseButtonState.Pressed)DragDrop.DoDragDrop(name,dragId,DragDropEffects.Move);};}
            if(CanJudge&&round==0){string targetMatch=m.Id,targetSlot=item.Slot;row.DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent(typeof(string))?DragDropEffects.Move:DragDropEffects.None;e.Handled=true;};row.Drop+=async(_,e)=>{if(e.Data.GetData(typeof(string))is string pilotId)await Command(new{id=t.Id,revision=t.Revision,action="moveSeed",userId=pilotId,targetMatch,targetSlot});};}
            row.Children.Add(name);
            if(CanJudge&&t.Phase!="archived"&&m.A!=null&&m.B!=null&&id!=m.Winner){string? target=id;var pick=Button("✓",async()=>await Command(new{id=t.Id,revision=t.Revision,action="winner",matchId=m.Id,winner=target,reason=""}));pick.Padding=new(8,3,8,3);pick.ToolTip="Назначить победителем";DockPanel.SetDock(pick,Dock.Right);row.Children.Insert(0,pick);}
            box.Children.Add(row);
        }
        var card=new Border{Child=box,BorderBrush=Paint.Brush(activePair==m.Id?"#79E8B4":m.Done?"#F20D4F":"#303034"),BorderThickness=new(activePair==m.Id?2:1),Padding=new(5),Background=Paint.Brush("#111113"),Cursor=System.Windows.Input.Cursors.Hand};card.MouseLeftButtonDown+=(_,e)=>{var source=e.OriginalSource as DependencyObject;while(source!=null){if(source is Button)return;source=VisualTreeHelper.GetParent(source);}activePair=m.Id;Render();e.Handled=true;};return card;
    }
    void RenderHistory(Tournament t)
    {
        var tools=new WrapPanel{Margin=new(0,0,0,18)};tools.Children.Add(Button("Экспорт результатов JSON",async()=>{var d=new Microsoft.Win32.SaveFileDialog{FileName="DriftStart-results.json",Filter="JSON (*.json)|*.json"};if(d.ShowDialog(dialogOwner??this)!=true)return;var history=new List<Audit>();string? before=(t.Revision+1).ToString("D8");do{var response=await api.Request("/api/history?id="+t.Id+"&before="+before);history.AddRange(response.GetProperty("audit").Deserialize<Audit[]>(CloudApi.JsonOptions)??[]);before=response.GetProperty("next").GetString();}while(before!=null);File.WriteAllText(d.FileName,JsonSerializer.Serialize(t with{Audit=history.ToArray()},new JsonSerializerOptions(CloudApi.JsonOptions){WriteIndented=true}));status.Text="Результаты и полный журнал экспортированы.";}));
        if(CanJudge&&t.Phase!="archived"){var b=Button("Закрыть турнир в архив",async()=>{var reason=Ask("Архив турнира","Причина закрытия (изменения будут запрещены)","",true);if(reason!=null)await Command(new{id=t.Id,revision=t.Revision,action="archive",reason});});b.Margin=new(12,0,0,0);tools.Children.Add(b);}content.Children.Add(tools);
        content.Children.Add(Text("Все турниры доступны в списке сверху. Журнал показывает автора, время и причину изменений.",12,"#8FA4BB"));
        foreach(var a in t.Audit){var row=new StackPanel();row.Children.Add(Text(a.Detail,15));if(!string.IsNullOrEmpty(a.Reason))row.Children.Add(Text("Причина: "+a.Reason,12,"#FFCA69"));row.Children.Add(Text($"{DateTimeOffset.FromUnixTimeMilliseconds((long)a.At).ToLocalTime():dd.MM.yyyy HH:mm:ss} · {a.Name} · Discord {a.Actor}",11,"#8FA4BB"));content.Children.Add(Card(row));}
        if(t.Revision+1>t.Audit.Length)content.Children.Add(Button("Загрузить более ранние записи",async()=>{var response=await api.Request("/api/history?id="+t.Id+"&before="+(t.Revision+1-t.Audit.Length).ToString("D8"));if(current?.Id!=t.Id||current.Revision!=t.Revision)return;current=t with{Audit=t.Audit.Concat(response.GetProperty("audit").Deserialize<Audit[]>(CloudApi.JsonOptions)??[]).ToArray()};Render();}));
    }
    void RenderJudges()
    {
        content.Children.Add(Card(Text(api.Me?.Role=="leader"?"Назначьте трёх судей: траектория, угол и стиль. Одну дисциплину может занимать только один судья.":"Роли и дисциплины назначает лидер группы.",15)));
        foreach(var u in users){var root=new StackPanel();var head=new DockPanel();var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Right);
            if(api.Me?.Role=="leader"&&u.Role!="leader"){
                foreach(var item in new[]{("trajectory","Траектория"),("angle","Угол"),("style","Стиль")}){var discipline=item.Item1;var b=Button(item.Item2,()=>Run(async()=>await api.Request("/api/member-role",new{userId=u.Id,role="judge",discipline})),u.Role!="judge"||u.Discipline!=discipline);b.Margin=new(6,0,0,0);actions.Children.Add(b);}
                if(u.Role=="judge"){var unjudge=Button("Снять судью",()=>Run(async()=>await api.Request("/api/member-role",new{userId=u.Id,role="pilot"})));unjudge.Margin=new(6,0,0,0);actions.Children.Add(unjudge);}var helper=Button(u.Role=="cohost"?"Снять помощника":"Помощник",()=>Run(async()=>await api.Request("/api/member-role",new{userId=u.Id,role=u.Role=="cohost"?"pilot":"cohost"})));helper.Margin=new(6,0,0,0);actions.Children.Add(helper);var remove=Button("Удалить",async()=>{if(MessageBox.Show(dialogOwner??this,$"Удалить {u.Name} из группы?","Участники",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes)await Run(async()=>await api.Request("/api/member-remove",new{userId=u.Id}));});remove.Margin=new(6,0,0,0);actions.Children.Add(remove);
            }head.Children.Add(actions);var text=new StackPanel();text.Children.Add(Text((u.Profile?.Nickname??u.Name)+"  ·  "+u.RoleLabel,15,u.Role=="leader"?"#FF4B7D":"#ECF2F7"));text.Children.Add(Text($"@{u.Username}",11,"#858996"));head.Children.Add(text);root.Children.Add(head);content.Children.Add(Card(root));}
    }
    public void Demo(string view)
    {
        page=view;users=[new("100000000000000001","EGZOT","egzot","leader"),new("100000000000000002","SMOKE","smoke","judge")];api.PreviewAccount(users[0]);identity.Text="EGZOT · Лидер · ДЕМО";
        var entries=Enumerable.Range(1,8).Select(i=>new Entry("10000000000000000"+i,new[]{"EGZOT","SMOKE","NIGHTRUNNER","KAZE","YOKO","SLIDE","NORTH","GHOST"}[i-1],[98-i*1.3,90-i*.7],i,98-i*1.3,90-i*.7)).ToArray();
        current=new("demo","MIDNIGHT CUP / MAJESTIC",view=="qualification"?"qualification":"bracket",24,entries,entries,[[new("0-0",entries[0].Id,entries[7].Id,entries[0].Id,true,false),new("0-1",entries[3].Id,entries[4].Id,null,false,false),new("0-2",entries[1].Id,entries[6].Id,null,false,false),new("0-3",entries[2].Id,entries[5].Id,null,false,false)],[new("1-0",null,null,null,false,false),new("1-1",null,null,null,false,false)],[new("2-0",null,null,null,false,false)]],null,[new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),users[0].Id,"EGZOT","bracket","Создана сетка TOP 8","")]);selecting=true;picker.ItemsSource=new[]{new EventSummary("demo",current.Name,current.Phase,8,null)};picker.SelectedIndex=0;selecting=false;Render();status.Text="Демонстрационные данные · не результаты реального турнира";
    }
}


