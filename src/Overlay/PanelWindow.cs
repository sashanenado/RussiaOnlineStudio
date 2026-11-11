using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RussiaOnlineOverlay;

public sealed class PanelWindow : Window
{
    private readonly PanelDefinition _panel;
    public bool MoveMode {get;set;}

    public PanelWindow(PanelDefinition panel)
    {
        _panel=panel;
        Title=panel.Title;
        Width=Math.Clamp(panel.Width,220,1200);
        Height=260;
        Left=panel.X; Top=panel.Y;
        WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.CanResizeWithGrip;
        AllowsTransparency=true;
        Background=new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(panel.Opacity,.2,1)*255),18,23,30));
        Topmost=panel.Topmost;
        ShowInTaskbar=false;
        var root=new Border{BorderBrush=new SolidColorBrush(Color.FromArgb(190,201,166,107)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(14),Background=Background};
        var stack=new DockPanel();
        var title=new TextBlock{Text=panel.Title,FontWeight=FontWeights.Bold,FontSize=15*panel.Scale,Foreground=new SolidColorBrush(Color.FromRgb(220,190,135)),Margin=new Thickness(0,0,0,9)};
        DockPanel.SetDock(title,Dock.Top);stack.Children.Add(title);
        var body=new TextBlock{Text=panel.Text,TextWrapping=TextWrapping.Wrap,FontSize=14*panel.Scale,Foreground=Brushes.White};
        var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};stack.Children.Add(scroll);root.Child=stack;Content=root;
        MouseLeftButtonDown+=(_,e)=>{if(MoveMode&&e.ButtonState==MouseButtonState.Pressed)try{DragMove();}catch{}};
    }
}