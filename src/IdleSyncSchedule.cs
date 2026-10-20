using System;
namespace PlaytimeRclone
{
    // Event-triggered work resets the delay; quiet checks and failures back off.
    public sealed class IdleSyncSchedule
    {
        private int quietChecks;
        public void Reset(){System.Threading.Interlocked.Exchange(ref quietChecks,0);}
        public void Observe(bool quietOrFailed){if(quietOrFailed){if(quietChecks<10)System.Threading.Interlocked.Increment(ref quietChecks);}else Reset();}
        public int DelaySeconds(int minimum){return (int)Math.Min(Math.Max(minimum,900L),(long)minimum*(1L<<Math.Min(10,quietChecks)));}
    }
}
