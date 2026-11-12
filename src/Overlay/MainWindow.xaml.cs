using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RussiaOnlineOverlay;

public partial class MainWindow : Window
{
    private const int WM_HOTKEY=0x0312, MOD_NOREPEAT=0x4000;
    private const uint VK_F8=0x77,VK_F9=0x78,VK_F10=0x79,VK_F11=0x7A;
    private readonly List<PanelWindow> _windows=new();
    private readonly DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private HwndSource? _source; private FileSystemWatcher? _watcher;
    private bool _moveMode,_panelsChanged,_lawsChanged,_profileChanged;
    private string _activeProfile="main";
    private PanelWindow? _lawMemoWindow;
    private readonly string _dataDir=Path.Combine(AppContext.BaseDirectory,"Data");

    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint vk);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool UnregisterHotKey(IntPtr h,int id);

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick+=(_,_)=>{_refreshTimer.Stop();ProcessPendingRefresh();};
        Loaded+=(_,_)=>{_activeProfile=ReadActiveProfile();LoadPanelsAndLaws();StartWatcher();};
        SourceInitialized+=(_,_)=>RegisterKeys();
        Closed+=(_,_)=>{_watcher?.Dispose();foreach(var w in _windows.ToArray())w.CloseWithoutSaving();if(_source is not null){for(int i=1;i<=4;i++)UnregisterHotKey(_source.Handle,i);_source.RemoveHook(Hook);}};
    }

    private void StartWatcher()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            _watcher=new FileSystemWatcher(_dataDir){IncludeSubdirectories=true,Filter="*",NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size};
            _watcher.Changed+=OnDataChanged;_watcher.Created+=OnDataChanged;_watcher.Renamed+=OnDataRenamed;_watcher.EnableRaisingEvents=true;
        }catch(Exception ex){Status.Text="Автообновление недоступно: "+ex.Message;}
    }
    private void OnDataChanged(object s,FileSystemEventArgs e)=>QueueRefresh(e.FullPath);
    private void OnDataRenamed(object s,RenamedEventArgs e)=>QueueRefresh(e.FullPath);
    private void QueueRefresh(string path)
    {
        var name=Path.GetFileName(path);
        if(name.Equals("active_profile.txt",StringComparison.OrdinalIgnoreCase))_profileChanged=true;
        else if(name.Equals("laws_database.json",StringComparison.OrdinalIgnoreCase))_lawsChanged=true;
        else if(name.Equals("panels.json",StringComparison.OrdinalIgnoreCase))_panelsChanged=true;else return;
        try{Dispatcher.BeginInvoke(()=>{_refreshTimer.Stop();_refreshTimer.Start();},DispatcherPriority.Background);}catch{}
    }
    private void ProcessPendingRefresh()
    {
        if(_profileChanged)
        {
            _profileChanged=false;var next=ReadActiveProfile();
            if(!string.Equals(next,_activeProfile,StringComparison.OrdinalIgnoreCase)){_activeProfile=next;CloseAllWindows();LoadPanelsAndLaws();return;}
        }
        if(_panelsChanged){_panelsChanged=false;RefreshPanelWindows();}
        if(_lawsChanged){_lawsChanged=false;RefreshLawMemo();}
        Status.Text=$"Автообновление выполнено • окон: {_windows.Count}";
    }

    private string ReadActiveProfile()
    {
        var p=Path.Combine(_dataDir,"active_profile.txt");if(!File.Exists(p))return "main";
        var v=File.ReadAllText(p).Trim();return v is "main" or "deputy" or "personal"?v:"main";
    }
    private string ProfileFolder=>Path.Combine(_dataDir,"Profiles",_activeProfile);
    private string PanelsPath{get{var p=Path.Combine(ProfileFolder,"panels.json");return File.Exists(p)?p:Path.Combine(_dataDir,"panels.json");}}

    private string? FindLawDatabase(out JsonObject? bases)
    {
        bases=null;string? emptyPath=null;
        foreach(var path in new[]{Path.Combine(ProfileFolder,"laws_database.json"),Path.Combine(_dataDir,"laws_database.json")})
        {
            if(!File.Exists(path))continue;
            try
            {
                var root=JsonNode.Parse(File.ReadAllText(path)) as JsonObject;var b=root?["Bases"] as JsonObject;if(b is null)continue;
                bool has=b.Any(k=>k.Value is JsonArray a&&a.Any(n=>n is JsonObject o&&(!string.IsNullOrWhiteSpace(o["Number"]?.ToString())||!string.IsNullOrWhiteSpace(o["Title"]?.ToString())||!string.IsNullOrWhiteSpace(o["Text"]?.ToString()))));
                if(has){bases=b;return path;}emptyPath??=path;
            }catch{}
        }
        if(emptyPath is not null){try{bases=(JsonNode.Parse(File.ReadAllText(emptyPath)) as JsonObject)?["Bases"] as JsonObject;return emptyPath;}catch{}}
        return null;
    }

    private void LoadPanelsAndLaws()
    {
        LoadPanelFile();RefreshLawMemo();
        Status.Text=$"Загружено окон: {_windows.Count}. Памятка законов формируется из базы автоматически.";
    }
    private void LoadPanelFile()
    {
        var path=PanelsPath;if(!File.Exists(path))return;
        try
        {
            var arr=JsonNode.Parse(File.ReadAllText(path)) as JsonArray;if(arr is null)throw new InvalidDataException("Неверный формат panels.json");
            foreach(var n in arr)if(n is JsonObject o&&o["Visible"]?.GetValue<bool>()!=false){var w=new PanelWindow(PanelDefinition.Read(o),path){MoveMode=_moveMode};_windows.Add(w);w.Show();}
        }catch(Exception ex){Status.Text="Не удалось загрузить панели: "+ex.Message;}
    }

    private void RefreshPanelWindows()
    {
        var path=PanelsPath;if(!File.Exists(path)){foreach(var w in _windows.ToArray())w.CloseWithoutSaving();_windows.Clear();return;}
        try
        {
            var arr=JsonNode.Parse(File.ReadAllText(path)) as JsonArray;if(arr is null)return;
            var defs=arr.OfType<JsonObject>().Where(o=>o["Visible"]?.GetValue<bool>()!=false).Select(PanelDefinition.Read).ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
            foreach(var old in _windows.Where(w=>!w.IsLawMemo).ToArray())if(!defs.ContainsKey(old.PanelId)){old.CloseWithoutSaving();_windows.Remove(old);}
            foreach(var p in defs.Values)
            {
                var w=_windows.FirstOrDefault(x=>!x.IsLawMemo&&string.Equals(x.PanelId,p.Id,StringComparison.OrdinalIgnoreCase));
                if(w is null){w=new PanelWindow(p,path){MoveMode=_moveMode};_windows.Add(w);w.Show();}
                else{w.UpdateFromDefinition(p);if(!w.IsVisible)w.Show();}
            }
        }catch(Exception ex){Status.Text="Ошибка обновления панелей: "+ex.Message;}
    }

    private void RefreshLawMemo()
    {
        var path=FindLawDatabase(out var bases);if(path is null||bases is null){Status.Text="Не найдена база законов. Проверь Data/laws_database.json.";return;}
        var text=new StringBuilder();var count=0;
        foreach(var category in bases)
        {
            if(category.Value is not JsonArray articles)continue;var group=new StringBuilder();
            foreach(var node in articles)
            {
                if(node is not JsonObject a)continue;
                var number=a["Number"]?.ToString()??"";var title=a["Title"]?.ToString()??"";var body=a["Text"]?.ToString()??"";var note=a["Note"]?.ToString()??"";
                if(string.IsNullOrWhiteSpace(number)&&string.IsNullOrWhiteSpace(title)&&string.IsNullOrWhiteSpace(body))continue;
                var heading=string.Join(" — ",new[]{number,title}.Where(x=>!string.IsNullOrWhiteSpace(x)));if(heading.Length>0)group.AppendLine(heading);
                if(body.Length>0)group.AppendLine(body.Trim());if(note.Length>0)group.AppendLine("Заметка: "+note.Trim());group.AppendLine();count++;
            }
            if(group.Length>0){text.AppendLine("━━━━━━━━ "+category.Key.ToUpperInvariant()+" ━━━━━━━━");text.AppendLine(group.ToString());}
        }
        if(count==0){Status.Text=$"База найдена ({Path.GetFileName(path)}), но сохранённых статей нет.";return;}
        var memo=new PanelDefinition{Id="LAW-MEMO-"+_activeProfile,Title=$"ПАМЯТКА ЗАКОНОВ · {count} СТАТЕЙ",Text=text.ToString(),Width=620,Height=760,X=520,Y=90,Opacity=.97,Scale=1,Topmost=true};
        if(_lawMemoWindow is null){_lawMemoWindow=new PanelWindow(memo,null,true){MoveMode=_moveMode};_windows.Add(_lawMemoWindow);_lawMemoWindow.Show();}
        else{_lawMemoWindow.UpdateFromDefinition(memo);if(!_lawMemoWindow.IsVisible)_lawMemoWindow.Show();}
        Status.Text=$"Загружено окон: {_windows.Count}. Памятка законов обновлена: {count} статей.";
    }
    private void CloseAllWindows(){foreach(var w in _windows.ToArray())w.CloseWithoutSaving();_windows.Clear();_lawMemoWindow=null;}
    private void RegisterKeys(){var h=new WindowInteropHelper(this).Handle;_source=HwndSource.FromHwnd(h);_source.AddHook(Hook);RegisterHotKey(h,1,MOD_NOREPEAT,VK_F8);RegisterHotKey(h,2,MOD_NOREPEAT,VK_F9);RegisterHotKey(h,3,MOD_NOREPEAT,VK_F10);RegisterHotKey(h,4,MOD_NOREPEAT,VK_F11);}
    private IntPtr Hook(IntPtr h,int msg,IntPtr wp,IntPtr lp,ref bool handled)
    {
        if(msg==WM_HOTKEY){switch(wp.ToInt32()){case 1:ExitOverlay();break;case 2:ToggleAll();break;case 3:ShowControl();break;case 4:_moveMode=!_moveMode;foreach(var w in _windows)w.MoveMode=_moveMode;Status.Text=_moveMode?"Свободное перемещение включено; заголовки также перетаскиваются всегда.":"F11 выключен; перетаскивание за заголовок доступно всегда.";break;}handled=true;}return IntPtr.Zero;
    }
    private void ToggleAll(){bool show=_windows.Any(w=>!w.IsVisible);foreach(var w in _windows){if(show)w.Show();else w.Hide();}}
    private void ShowControl(){if(IsVisible)Hide();else{Show();WindowState=WindowState.Normal;Activate();}}
    private void Show_Click(object s,RoutedEventArgs e){foreach(var w in _windows)w.Show();}
    private void Hide_Click(object s,RoutedEventArgs e){foreach(var w in _windows)w.Hide();}
    private void Exit_Click(object s,RoutedEventArgs e)=>ExitOverlay();
    private void ExitOverlay()=>Application.Current.Shutdown();
}

public sealed class PanelDefinition
{
    public string Id{get;set;}="";public string Title{get;set;}="";public string Text{get;set;}="";
    public double Width{get;set;}=360;
    public double Height{get;set;}=260;
    public double Scale{get;set;}=1;
    public double X{get;set;}=80;
    public double Y{get;set;}=100;
    public double Opacity{get;set;}=.94;
    public bool Topmost{get;set;}=true;
    public static PanelDefinition Read(JsonObject o)=>new(){Id=o["Id"]?.ToString()??"",Title=o["Title"]?.ToString()??"Памятка",Text=o["Text"]?.ToString()??o["Body"]?.ToString()??"",Width=N(o,"Width",360),Height=N(o,"Height",260),Scale=N(o,"Scale",1),X=N(o,"X",80),Y=N(o,"Y",100),Opacity=N(o,"Opacity",.94),Topmost=o["Topmost"]?.GetValue<bool>()??true};
    private static double N(JsonObject o,string k,double d)=>double.TryParse(o[k]?.ToString(),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var v)?v:d;
}