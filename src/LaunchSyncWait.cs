using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PlaytimeRclone
{
    public static class LaunchSyncWait
    {
        // True means the active cycle checked this game; false transfers the gate to the caller.
        public static async Task<bool> ReuseOrEnter(SemaphoreSlim gate, Func<bool> targetReady, int maximumMilliseconds)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                if (targetReady()) return true;
                var remaining = Math.Max(0, maximumMilliseconds - (int)watch.ElapsedMilliseconds);
                if (await gate.WaitAsync(Math.Min(50, remaining))) return false;
            } while (watch.ElapsedMilliseconds < maximumMilliseconds);
            if (targetReady()) return true;
            throw new TimeoutException("Another sync is still running.");
        }
    }
}
