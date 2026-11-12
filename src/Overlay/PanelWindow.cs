using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RussiaOnlineOverlay;

public sealed class PanelWindow : Window
{
    private readonly PanelDefinition _panel;
    private readonly string? _sourcePath;
    private readonly DispatcherTimer _saveTimer;
    private readonly TextBlock _heading;
    private readonly TextBlock _body;
    private bool _isClosing, _skipSave;
    public bool MoveMode { get; set; }
    public string PanelId => _panel.Id;
    public bool IsLawMemo { get; }

    public PanelWindow(PanelDefinition panel, string? sourcePath = null, bool isLawMemo = false)
    {
        _panel=panel; _sourcePath=sourcePath; IsLawMemo=isLawMemo;
        Title=panel.Title; Width=Math.Clamp(panel.Width,220,1200); Height=Math.Clamp(panel.Height,160,1200);
        Left=panel.X; Top=panel.Y; WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.CanResizeWithGrip;
        AllowsTransparency=true; Background=new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(panel.Opacity,.2,1)*255),18,23,30));
        Topmost=panel.Topmost; ShowInTaskbar=false;
        var root=new Border{BorderBrush=new SolidColorBrush(Color.FromArgb(210,201,166,107)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Background=Background};
        var layout=new DockPanel();
        _heading=new TextBlock{Text=panel.Title,FontWeight=FontWeights.Bold,FontSize=15*panel.Scale,Foreground=new SolidColorBrush(Color.FromRgb(235,204,151)),TextTrimming=TextTrimming.CharacterEllipsis};
        var header=new Border{Background=new SolidColorBrush(Color.FromArgb(25,201,166,107)),CornerRadius=new CornerRadius(5),Padding=new Thickness(7,4,7,7),Cursor=Cursors.SizeAll,Child=_heading};
        DockPanel.SetDock(header,Dock.Top); layout.Children.Add(header);
        header.MouseLeftButtonDown+=(_,e)=>{if(e.ButtonState==MouseButtonState.Pressed){try{DragMove();}catch{} e.Handled=true;}};
        _body=new TextBlock{Text=panel.Text,TextWrapping=TextWrapping.Wrap,FontSize=14*panel.Scale,Foreground=Brushes.White};
        layout.Children.Add(new ScrollViewer{Content=_body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        root.Child=layout; Content=root;
        _saveTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
        _saveTimer.Tick+=(_,_)=>{_saveTimer.Stop();SavePosition();};
        LocationChanged+=(_,_)=>ScheduleSave(); SizeChanged+=(_,_)=>ScheduleSave();
        Closing+=(_,_)=>{_isClosing=true;_saveTimer.Stop();if(!_skipSave)SavePosition();};
        PreviewMouseLeftButtonDown+=(_,e)=>{if(!MoveMode||e.OriginalSource is not DependencyObject d)return;if(FindAncestor<ScrollViewer>(d)is null&&FindAncestor<TextBox>(d)is null){try{DragMove();}catch{}e.Handled=true;}};
    }

    public void UpdateFromDefinition(PanelDefinition p)
    {
        _panel.Title=p.Title;_panel.Text=p.Text;_panel.Width=p.Width;_panel.Height=p.Height;_panel.Scale=p.Scale;_panel.Opacity=p.Opacity;_panel.Topmost=p.Topmost;
        Title=p.Title;_heading.Text=p.Title;_heading.FontSize=15*p.Scale;_body.Text=p.Text;_body.FontSize=14*p.Scale;
        Width=Math.Clamp(p.Width,220,1200);Height=Math.Clamp(p.Height,160,1200);Topmost=p.Topmost;
        Background=new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(p.Opacity,.2,1)*255),18,23,30));
    }
    public void CloseWithoutSaving(){_skipSave=true;Close();}
    private void ScheduleSave(){if(_isClosing||_skipSave||string.IsNullOrWhiteSpace(_sourcePath))return;_saveTimer.Stop();_saveTimer.Start();}
    private void SavePosition()
    {
        if(_skipSave||string.IsNullOrWhiteSpace(_sourcePath)||!File.Exists(_sourcePath))return;
        try
        {
            _panel.X=Left;_panel.Y=Top;_panel.Width=Width;_panel.Height=Height;
            var root=JsonNode.Parse(File.ReadAllText(_sourcePath)) as JsonArray;if(root is null)return;
            foreach(var n in root)if(n is JsonObject o&&string.Equals(o["Id"]?.ToString(),_panel.Id,StringComparison.OrdinalIgnoreCase))
            {o["X"]=_panel.X;o["Y"]=_panel.Y;o["Width"]=_panel.Width;o["Height"]=_panel.Height;o["Opacity"]=_panel.Opacity;break;}
            var tmp=_sourcePath+".overlay.tmp";File.WriteAllText(tmp,root.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,_sourcePath,true);
        }catch{}
    }
    private static T? FindAncestor<T>(DependencyObject? d) where T:DependencyObject{while(d is not null){if(d is T t)return t;d=VisualTreeHelper.GetParent(d);}return null;}
}