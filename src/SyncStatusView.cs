using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace PlaytimeRclone
{
 public sealed partial class PlaytimePlugin
 {
  private readonly SyncProgress syncProgress=new SyncProgress();
  private string ReportStatus(string message,SyncDisplayState state=SyncDisplayState.Ready,string title=null,int current=0,int total=0)
  {status=message;syncProgress.Report(state,title??state.ToString(),message,current,total);return message;}
  private TextBlock SyncStatusView(Panel parent)
  {
   var panel=new StackPanel{Margin=new Thickness(0,8,0,10)};parent.Children.Add(panel);
   var header=new DockPanel();panel.Children.Add(header);
   var time=new TextBlock{Foreground=Muted,FontSize=12,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(time,Dock.Right);header.Children.Add(time);
   var heading=new TextBlock{Foreground=Ink,FontSize=15,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};header.Children.Add(heading);
   var bar=new ProgressBar{Height=6,IsIndeterminate=true,Minimum=0,Maximum=100,Foreground=Accent,Background=Surface,Margin=new Thickness(0,10,0,10),Visibility=Visibility.Collapsed};panel.Children.Add(bar);
   // Theme ProgressBar templates can hide the moving indicator. Keep the actual
   // progress control for accessibility, with an explicit theme-coloured pulse.
   var activityBrush=Contrast(Accent,Surface)>=3?Accent:ThemeBrush("GlyphBrush",Ink);if(Contrast(activityBrush,Surface)<3)activityBrush=Ink;
   var track=new FrameworkElementFactory(typeof(Grid));track.SetValue(UIElement.ClipToBoundsProperty,true);
   var background=new FrameworkElementFactory(typeof(Border));background.SetValue(Border.BackgroundProperty,activityBrush);background.SetValue(UIElement.OpacityProperty,0.18);track.AppendChild(background);
   var pulse=new FrameworkElementFactory(typeof(Border),"ActivityPulse");pulse.SetValue(Border.BackgroundProperty,activityBrush);pulse.SetValue(FrameworkElement.WidthProperty,110.0);pulse.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Left);pulse.SetValue(UIElement.RenderTransformProperty,new TranslateTransform());track.AppendChild(pulse);
   bar.Template=new ControlTemplate(typeof(ProgressBar)){VisualTree=track};
   Action animate=()=>{bar.ApplyTemplate();var part=bar.Template.FindName("ActivityPulse",bar) as Border;if(part==null)return;var move=part.RenderTransform as TranslateTransform;if(move==null)return;if(move.IsFrozen){move=move.Clone();part.RenderTransform=move;}if(!bar.IsVisible){move.BeginAnimation(TranslateTransform.XProperty,null);return;}move.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(-110,Math.Max(110,bar.ActualWidth),TimeSpan.FromSeconds(1.4)){RepeatBehavior=RepeatBehavior.Forever});};
   bar.Loaded+=(_,e)=>animate();bar.IsVisibleChanged+=(_,e)=>animate();bar.SizeChanged+=(_,e)=>animate();bar.Unloaded+=(_,e)=>{var part=bar.Template.FindName("ActivityPulse",bar) as Border;if(part!=null&&!part.RenderTransform.IsFrozen)((TranslateTransform)part.RenderTransform).BeginAnimation(TranslateTransform.XProperty,null);};
   var detail=Label(panel,status,14);var hint=Label(panel,"",12,true);
   Action update=()=>{
    var snapshot=syncProgress.Read();var busy=syncing!=0||connectionTesting||walkthroughWorking||setupChecking;
    var known=snapshot.Message==status;var active=busy;
    heading.Text=known?snapshot.Title:active?"Working in the background":IsSyncPaused?"Paused":"Ready";
    if(known&&snapshot.Total>0)heading.Text+=" · "+snapshot.Current+" of "+snapshot.Total+" games";
    detail.Text=status;detail.Visibility=String.IsNullOrWhiteSpace(status)||status==heading.Text?Visibility.Collapsed:Visibility.Visible;bar.Visibility=active?Visibility.Visible:Visibility.Collapsed;
    time.Visibility=active?Visibility.Visible:Visibility.Collapsed;time.Text=active?TimeSpan.FromSeconds(snapshot.ElapsedSeconds).ToString(@"m\:ss")+" elapsed":"";
    if(active)hint.Text="";
    else if(known&&snapshot.State==SyncDisplayState.Completed)hint.Text="";
    else if(known&&snapshot.State==SyncDisplayState.Waiting)hint.Text="Changes queued. Will retry.";
    else if(known&&snapshot.State==SyncDisplayState.Failed)hint.Text="Check your connection and retry.";
    else if(known&&snapshot.State==SyncDisplayState.Review)hint.Text="Review the affected games before continuing.";
    else if(IsSyncPaused||known&&snapshot.State==SyncDisplayState.Paused)hint.Text="";
    else hint.Text="";
    hint.Visibility=String.IsNullOrWhiteSpace(hint.Text)?Visibility.Collapsed:Visibility.Visible;
   };
   var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};timer.Tick+=(_,e)=>update();panel.Loaded+=(_,e)=>{update();timer.Start();};panel.Unloaded+=(_,e)=>timer.Stop();update();return detail;
  }
 }
}
