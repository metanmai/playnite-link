using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlaytimeRclone
{
    // Covers every WPF window in this Playnite process, including other add-ons.
    // Game launches have a separate SDK guard in Plugin.OnGameStarting.
    public sealed class ApplicationLock : IDisposable
    {
        private sealed class SurfaceState {public double Opacity;public bool Enabled;}
        private readonly Application app;
        private readonly Func<bool> needsLock;
        private readonly Func<Window> createGate;
        private readonly Action quit;
        private readonly Action<Exception> onError;
        private readonly Dictionary<Window,SurfaceState> covered=new Dictionary<Window,SurfaceState>();
        private readonly DispatcherTimer timer;
        private Window gate;
        private bool disposed,enforcing,renderHook,failed;
        public bool IsLocked{get{return needsLock();}}
        public Window Gate{get{return gate;}}
        public ApplicationLock(Application app,Func<bool> needsLock,Func<Window> createGate,Action quit,Action<Exception> onError=null)
        {
            this.app=app;this.needsLock=needsLock;this.createGate=createGate;this.quit=quit;this.onError=onError;
            timer=new DispatcherTimer(DispatcherPriority.Send,app.Dispatcher){Interval=TimeSpan.FromMilliseconds(250)};
            timer.Tick+=Tick;app.Activated+=Activated;InputManager.Current.PreProcessInput+=Input;
            timer.Start();Enforce();
        }
        private void Tick(object sender,EventArgs args){Enforce();}
        private void Render(object sender,EventArgs args){Enforce();}
        private void Activated(object sender,EventArgs args){Enforce();if(gate!=null&&needsLock())gate.Activate();}
        private void Input(object sender,PreProcessInputEventArgs args)
        {
            if(!needsLock())return;
            var input=args.StagingItem.Input;
            var source=(input.OriginalSource??input.Source) as DependencyObject;
            // Raw input reports have no routed target yet. Disabling covered windows
            // handles those; cancelling raw reports here would also break gate typing.
            if(source==null)return;
            if(source!=null&&Window.GetWindow(source)==gate)return;
            if(input is KeyboardEventArgs||input is MouseEventArgs||input is TextCompositionEventArgs||input is TouchEventArgs)args.Cancel();Enforce();
        }
        public void Enforce()
        {
            if(!app.Dispatcher.CheckAccess()){app.Dispatcher.BeginInvoke(new Action(Enforce));return;}
            if(disposed||failed||enforcing||app.Dispatcher.HasShutdownStarted)return;enforcing=true;
            try
            {
                if(!needsLock()){Restore();return;}
                if(!renderHook){CompositionTarget.Rendering+=Render;renderHook=true;}
                var activate=app.Windows.OfType<Window>().Any(w=>w.IsActive);
                if(gate==null)
                {
                    var main=app.MainWindow;gate=createGate();if(app.MainWindow==gate)app.MainWindow=main;gate.ShowActivated=activate;
                    gate.Closing+=GateClosing;gate.Closed+=GateClosed;
                }
                foreach(var window in app.Windows.OfType<Window>().ToArray())
                {
                    if(window==gate)continue;
                    var animated=DependencyPropertyHelper.GetValueSource(window,UIElement.OpacityProperty).IsAnimated;
                    SurfaceState saved;
                    if(!covered.TryGetValue(window,out saved))covered.Add(window,new SurfaceState{Opacity=window.Opacity,Enabled=window.IsEnabled});
                    else
                    {
                        // Startup/modal dialogs may finish while locked and re-enable
                        // their owner. Remember that newer state before shielding again.
                        if(window.IsEnabled)saved.Enabled=true;
                        if(!animated&&window.Opacity!=0)saved.Opacity=window.Opacity;
                    }
                    if(animated)
                    {
                        saved=covered[window];var basis=(double)window.GetAnimationBaseValue(UIElement.OpacityProperty);
                        saved.Opacity=basis>0?basis:saved.Opacity>0?saved.Opacity:1;
                        // Animated values outrank local values in WPF. Stop window fades
                        // so a theme cannot animate the hidden library back into view.
                        window.BeginAnimation(UIElement.OpacityProperty,null);
                    }
                    window.SetCurrentValue(UIElement.OpacityProperty,0.0);window.SetCurrentValue(UIElement.IsEnabledProperty,false);
                }
                if(!gate.IsVisible)gate.Show();
            }
            catch(Exception ex)
            {
                if(onError!=null)onError(ex);
                // A broken unlock surface must close the app, not disable protection.
                failed=true;timer.Stop();
                foreach(var window in app.Windows.OfType<Window>().ToArray())if(window!=gate){window.Opacity=0;window.IsEnabled=false;}
                if(renderHook){CompositionTarget.Rendering-=Render;renderHook=false;}quit();
            }
            finally{enforcing=false;}
        }
        private void GateClosing(object sender,System.ComponentModel.CancelEventArgs args)
        {
            if(disposed||!needsLock()||app.Dispatcher.HasShutdownStarted)return;
            args.Cancel=true;quit();
        }
        private void GateClosed(object sender,EventArgs args){if(sender==gate)gate=null;}
        private void Restore()
        {
            if(renderHook){CompositionTarget.Rendering-=Render;renderHook=false;}
            foreach(var pair in covered.ToArray())
            {pair.Key.SetCurrentValue(UIElement.OpacityProperty,pair.Value.Opacity);pair.Key.SetCurrentValue(UIElement.IsEnabledProperty,pair.Value.Enabled);}
            covered.Clear();if(gate!=null){var old=gate;gate=null;old.Closing-=GateClosing;old.Close();}
        }
        public void Dispose()
        {
            disposed=true;timer.Stop();app.Activated-=Activated;InputManager.Current.PreProcessInput-=Input;Restore();
        }
    }
}
