using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Generic;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private ApplicationLock applicationLock;
        private string protectionError;
        public bool IsApplicationLocked{get{return protectionError!=null||(security!=null&&!security.IsUnlocked);}}
        private void InitializeApplicationProtection()
        {
            var app=Application.Current;if(app==null)return;
            try{applicationLock=new ApplicationLock(app,()=>IsApplicationLocked,CreateApplicationGate,QuitLockedApplication,ex=>logger.Error(ex,"App lock failed; closing Playnite."));}
            catch(Exception ex){protectionError=ex.Message;logger.Error(ex,"App lock could not initialize; closing Playnite.");app.Dispatcher.BeginInvoke(new Action(()=>app.Shutdown()));}
            logger.Info("App-wide Playnite protection initialized; "+(IsApplicationLocked?"locked":"unlocked")+".");
            Microsoft.Win32.SystemEvents.SessionSwitch+=WindowsSessionChanged;
        }
        private void QuitLockedApplication()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(()=>{
                closing=true;if(applicationLock!=null)applicationLock.Dispose();Application.Current.Shutdown();
            }));
        }
        private void LockApplication()
        {
            if(security!=null)security.Lock();
            if(applicationLock==null)return;
            if(Application.Current.Dispatcher.CheckAccess())applicationLock.Enforce();
            else Application.Current.Dispatcher.BeginInvoke(new Action(()=>applicationLock.Enforce()));
        }
        private static IEnumerable<T> GateElements<T>(DependencyObject parent)where T:DependencyObject
        {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var item in GateElements<T>(child))yield return item;}}
        private static void RefreshGateTheme(Window window)
        {
            window.Background=Canvas;window.Foreground=Ink;
            foreach(var label in GateElements<TextBlock>(window))if(label.Tag is bool)label.Foreground=(bool)label.Tag?Muted:Ink;
            foreach(var password in GateElements<PasswordBox>(window)){password.Background=Surface;password.Foreground=Ink;password.BorderBrush=Line;password.CaretBrush=Ink;}
            foreach(var button in GateElements<Button>(window))StyleAction(button,Convert.ToString(button.Content)=="Unlock Playnite");
        }
        private Window CreateApplicationGate()
        {
            var window=new Window{Title="Unlock Playnite",Background=Canvas,Foreground=Ink,WindowStyle=WindowStyle.ToolWindow,
                ResizeMode=ResizeMode.NoResize,WindowState=WindowState.Normal,ShowInTaskbar=true,MinWidth=360,MinHeight=360,
                WindowStartupLocation=WindowStartupLocation.CenterScreen,Width=Math.Min(560,Math.Max(360,SystemParameters.WorkArea.Width-32)),Height=Math.Min(540,Math.Max(360,SystemParameters.WorkArea.Height-32))};
            ApplyTheme(window,Control.BackgroundProperty,"PopupBackgroundBrush",Canvas);ApplyTheme(window,Control.ForegroundProperty,"TextBrush",Ink);
            var panel=new StackPanel{Margin=new Thickness(28)};
            Label(panel,"PLAYNITE",13,true);Label(panel,"Welcome back",32).FontWeight=FontWeights.SemiBold;
            Label(panel,"Unlock Playnite to access your library, settings and games.",15,true);
            var feedback=Label(panel,"",14);bool busy=false;
            if(protectionError==null&&security!=null)
            {
                Label(panel,"Passcode",13,true);var password=new PasswordBox{Padding=new Thickness(12),MinHeight=48,FontSize=18,VerticalContentAlignment=VerticalAlignment.Center,Background=Surface,Foreground=Ink,BorderBrush=Line,Margin=new Thickness(0,0,0,16)};ApplyTheme(password,Control.BackgroundProperty,"ButtonBackgroundBrush",Surface);ApplyTheme(password,Control.ForegroundProperty,"TextBrush",Ink);ApplyTheme(password,Control.BorderBrushProperty,"PopupBorderBrush",Line);panel.Children.Add(password);
                var unlock=SecurityButton(panel,"Unlock Playnite",async()=>{
                    if(busy)return;busy=true;var value=password.Password;password.Clear();feedback.Text="Verifying…";
                    try
                    {
                        if(await Task.Run(()=>security.Verify(value))){applicationLock.Enforce();}
                        else feedback.Text="Incorrect passcode. Try again.";
                    }
                    catch(Exception ex){feedback.Text=ex.Message;}
                    finally{value=null;busy=false;if(window.IsVisible&&!security.IsUnlocked)password.Focus();}
                });StyleAction(unlock,true);
                password.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;unlock.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));}};
                window.Loaded+=(_,e)=>password.Focus();
            }
            else feedback.Text="Protection settings could not be read. Quit Playnite and restore this profile's protected settings from backup. The app remains locked.";
            SecurityButton(panel,"Quit Playnite",QuitLockedApplication);
            Label(panel,"Your library stays hidden until you unlock. Background synchronization can continue.",13,true);
            window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape)e.Handled=true;};
            var themeRefresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};int themeChecks=0;
            themeRefresh.Tick+=(_,e)=>{if(!window.IsVisible){themeRefresh.Stop();return;}RefreshGateTheme(window);if(++themeChecks>=40)themeRefresh.Stop();};
            window.Loaded+=(_,e)=>{RefreshGateTheme(window);themeRefresh.Start();};window.Closed+=(_,e)=>themeRefresh.Stop();
            window.Content=new Grid{Children={new ScrollViewer{MaxWidth=560,Content=panel,VerticalAlignment=VerticalAlignment.Center,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}}};return window;
        }
    }
}
