using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AiVoiceBridge.Core;
using AiVoiceBridge.ViewModels;
namespace AiVoiceBridge;
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly WindowsDevices _devices=new();
    private readonly AudioEngine _engine=new();
    private readonly RawHotkey _hotkey=new();
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon? _trayIcon;
    private bool _closed;
    public MainWindow()
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI Voice Bridge","settings.json");
        _viewModel=new(_devices,_engine,_hotkey,new JsonSettingsStore(path));
        InitializeComponent(); DataContext=_viewModel;
        _trayIcon=Environment.ProcessPath is { } executable ? System.Drawing.Icon.ExtractAssociatedIcon(executable) : null;
        _tray=new(){Icon=_trayIcon??System.Drawing.SystemIcons.Application,Text="AI Voice Bridge",Visible=false};
        var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть",null,(_,_)=>Dispatcher.Invoke(Restore));
        menu.Items.Add("Отключить звук",null,(_,_)=>Dispatcher.Invoke(()=>_viewModel.EmergencyCommand.Execute(null)));
        menu.Items.Add("Выход",null,(_,_)=>Dispatcher.Invoke(Close));
        _tray.ContextMenuStrip=menu; _tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(Restore);
        SourceInitialized+=(_,_)=> { try { _hotkey.Attach(new WindowInteropHelper(this).Handle); } catch(Exception e) { _viewModel.Report("Raw Input недоступен: "+e.Message); } };
        StateChanged+=(_,_)=> { if(WindowState==WindowState.Minimized) { _viewModel.SetVisible(false); if(_viewModel.Settings.MinimizeToTray) { _tray.Visible=true; Hide(); } } else _viewModel.SetVisible(true); };
        Loaded+=(_,_)=> { if(_viewModel.Settings.StartMinimized)WindowState=WindowState.Minimized; };
        Microsoft.Win32.SystemEvents.PowerModeChanged+=PowerChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch+=SessionChanged;
    }
    private void PowerChanged(object sender,Microsoft.Win32.PowerModeChangedEventArgs e) { if(e.Mode==Microsoft.Win32.PowerModes.Suspend)Dispatcher.BeginInvoke(()=>_viewModel.EmergencyCommand.Execute(null)); }
    private void SessionChanged(object sender,Microsoft.Win32.SessionSwitchEventArgs e) { if(e.Reason==Microsoft.Win32.SessionSwitchReason.SessionLock)Dispatcher.BeginInvoke(()=>_viewModel.EmergencyCommand.Execute(null)); }
    private void Restore() { Show(); WindowState=WindowState.Normal; _tray.Visible=false; Activate(); _viewModel.SetVisible(true); }
    private void Demo_Down(object sender,MouseButtonEventArgs e) { DemoHold.CaptureMouse(); _viewModel.BeginDemo(); e.Handled=true; }
    private void Demo_Up(object sender,MouseButtonEventArgs e) { _viewModel.ReleaseDemo(); DemoHold.ReleaseMouseCapture(); e.Handled=true; }
    private void Demo_KeyDown(object sender,KeyEventArgs e) { if(e.Key==Key.Space) { _viewModel.BeginDemo(); e.Handled=true; } }
    private void Demo_KeyUp(object sender,KeyEventArgs e) { if(e.Key==Key.Space) { _viewModel.ReleaseDemo(); e.Handled=true; } }
    private void Demo_Release(object sender,RoutedEventArgs e)=>_viewModel.ReleaseDemo();
    private void Window_Deactivated(object? sender,EventArgs e) { _viewModel.ReleaseDemo(); if(DemoHold.IsMouseCaptured)DemoHold.ReleaseMouseCapture(); }
    private void Tabs_SelectionChanged(object sender,SelectionChangedEventArgs e) { if(e.Source is TabControl)_viewModel.ReleaseDemo(); }
    private void Audio_SelectionChanged(object sender,SelectionChangedEventArgs e) { if(IsLoaded && e.Source is ComboBox)_viewModel.AudioSelectionChanged(); }
    private void Route_Changed(object sender,RoutedEventArgs e) { if(IsLoaded)_viewModel.AudioSelectionChanged(); }
    private async void Window_Closing(object? sender,CancelEventArgs e)
    {
        if(_closed)return;
        e.Cancel=true; IsEnabled=false;
        await _viewModel.StopAsync();
        if(!_viewModel.Save() && MessageBox.Show(this,"Закрыть без сохранения?","AI Voice Bridge",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) { IsEnabled=true; return; }
        _closed=true; _viewModel.DetachMeter(); _hotkey.Dispose(); _devices.Dispose(); _tray.Dispose(); _trayIcon?.Dispose();
        Microsoft.Win32.SystemEvents.PowerModeChanged-=PowerChanged; Microsoft.Win32.SystemEvents.SessionSwitch-=SessionChanged;
        Close();
    }
}

