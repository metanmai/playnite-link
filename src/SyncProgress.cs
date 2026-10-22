using System;
using System.Diagnostics;
namespace PlaytimeRclone
{
 public enum SyncDisplayState {Ready,Working,Stopping,Paused,Completed,Failed,Review,Waiting}
 public sealed class SyncProgressSnapshot
 {
  public SyncDisplayState State;public string Title,Message;public int Current,Total;public double ElapsedSeconds,StageSeconds;
  public bool Busy {get{return State==SyncDisplayState.Working||State==SyncDisplayState.Stopping;}}
 }
 public sealed class SyncProgress
 {
  readonly object gate=new object();readonly Func<double> clock;SyncDisplayState state;string title="Ready",message="";int current,total;double started,stageStarted,elapsed,stageElapsed;
  public SyncProgress(Func<double> clock=null){this.clock=clock??(()=>((double)Stopwatch.GetTimestamp()/Stopwatch.Frequency));}
  public void Report(SyncDisplayState next,string heading,string text,int done=0,int count=0)
  {
   lock(gate){var now=clock();var wasBusy=state==SyncDisplayState.Working||state==SyncDisplayState.Stopping;var busy=next==SyncDisplayState.Working||next==SyncDisplayState.Stopping;
    if(busy&&!wasBusy){started=now;stageStarted=now;elapsed=0;stageElapsed=0;}
    else if(wasBusy){elapsed=Math.Max(0,now-started);stageElapsed=Math.Max(0,now-stageStarted);}
    if(heading!=title){stageStarted=now;stageElapsed=0;}
    if(next==SyncDisplayState.Ready){elapsed=0;stageElapsed=0;}
    state=next;title=heading;message=text;total=Math.Max(0,count);current=Math.Max(0,Math.Min(done,total));}
  }
  public SyncProgressSnapshot Read(){lock(gate){var now=clock();var busy=state==SyncDisplayState.Working||state==SyncDisplayState.Stopping;return new SyncProgressSnapshot{State=state,Title=title,Message=message,Current=current,Total=total,ElapsedSeconds=busy?Math.Max(0,now-started):elapsed,StageSeconds=busy?Math.Max(0,now-stageStarted):stageElapsed};}}
 }
}
