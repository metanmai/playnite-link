using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class LocalLockState
    {
        public int Version=1;
        public string Salt,Hash;
        public int Iterations=600000;
        public int FailedAttempts;
        public string LockedUntilUtc;
        public int UnlockMinutes=5;
    }
    public sealed class LocalSecurity
    {
        private readonly string path;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate=new object();
        private LocalLockState state;
        private long unlockedUntilTicks;
        private volatile bool configured;
        private int generation;
        public LocalSecurity(string directory,Func<DateTimeOffset> clock=null)
        {
            this.clock=clock??(()=>DateTimeOffset.UtcNow);path=Path.Combine(directory,"local-security.dpapi");Directory.CreateDirectory(directory);
            if(File.Exists(path))
            {
                var encrypted=Convert.FromBase64String(File.ReadAllText(path));var json=ProtectedData.Unprotect(encrypted,Encoding.UTF8.GetBytes("PlayniteLink.LocalLock.v1"),DataProtectionScope.CurrentUser);
                try{state=JsonConvert.DeserializeObject<LocalLockState>(Encoding.UTF8.GetString(json));}finally{Array.Clear(json,0,json.Length);}
                if(state==null||state.Version!=1||state.Iterations<600000||state.Iterations>2000000||state.UnlockMinutes<1||state.UnlockMinutes>60||state.FailedAttempts<0||
                    Convert.FromBase64String(state.Salt??"").Length!=32||Convert.FromBase64String(state.Hash??"").Length!=32)throw new InvalidDataException("Local security settings are damaged. Protection was not silently disabled.");
            }
            configured=state!=null;
        }
        public bool Enabled{get{return configured;}}
        public int Generation{get{return Thread.VolatileRead(ref generation);}}
        public int UnlockMinutes{get{lock(gate)return state==null?5:state.UnlockMinutes;}}
        public bool IsUnlocked{get{return !configured||clock().UtcDateTime.Ticks<Interlocked.Read(ref unlockedUntilTicks);}}
        private void Unlock(int minutes,int expectedGeneration)
        {if(expectedGeneration!=Generation)throw new OperationCanceledException("Playnite was locked during verification. Try again.");Interlocked.Exchange(ref unlockedUntilTicks,clock().AddMinutes(minutes).UtcDateTime.Ticks);if(expectedGeneration!=Generation)Interlocked.Exchange(ref unlockedUntilTicks,0);}
        private void Save()
        {
            if(state==null){if(File.Exists(path))File.Delete(path);return;}
            var raw=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state));byte[] encrypted;
            try{encrypted=ProtectedData.Protect(raw,Encoding.UTF8.GetBytes("PlayniteLink.LocalLock.v1"),DataProtectionScope.CurrentUser);}finally{Array.Clear(raw,0,raw.Length);}
            Atomic.Write(path,Convert.ToBase64String(encrypted));
            // Atomic.Write is text-based; store DPAPI bytes through its base64 representation.
        }
        static byte[] Derive(string passcode,byte[] salt,int iterations)
        {using(var kdf=new Rfc2898DeriveBytes(passcode,salt,iterations,HashAlgorithmName.SHA256))return kdf.GetBytes(32);}
        public static void ValidatePasscode(string value)
        {if(value==null||value.Length<6||value.Length>128||String.IsNullOrWhiteSpace(value))throw new ArgumentException("Choose a passcode or passphrase with 6–128 characters.");}
        public void Configure(string passcode,int minutes)
        {
            ValidatePasscode(passcode);if(minutes<1||minutes>60)throw new ArgumentException("Unlock duration must be 1–60 minutes.");
            lock(gate)
            {
                if(state!=null&&!IsUnlocked)throw new UnauthorizedAccessException("Unlock before changing local protection.");
                var attemptGeneration=Generation;var previous=state;
                var salt=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(salt);
                var hash=Derive(passcode,salt,600000);try{state=new LocalLockState{Salt=Convert.ToBase64String(salt),Hash=Convert.ToBase64String(hash),UnlockMinutes=minutes};try{Save();}catch{state=previous;throw;}configured=true;Unlock(minutes,attemptGeneration);}finally{Array.Clear(hash,0,hash.Length);}
            }
        }
        public bool Verify(string passcode)
        {
            lock(gate)
            {
                if(state==null)return true;var attemptGeneration=Generation;DateTimeOffset locked;
                if(DateTimeOffset.TryParse(state.LockedUntilUtc,out locked)&&clock()<locked)throw new UnauthorizedAccessException("Too many attempts. Try again after "+locked.ToLocalTime().ToString("T")+".");
                var actual=Derive(passcode??"",Convert.FromBase64String(state.Salt),state.Iterations);var expected=Convert.FromBase64String(state.Hash);int diff=0;
                try{for(int i=0;i<expected.Length;i++)diff|=expected[i]^actual[i];}finally{Array.Clear(actual,0,actual.Length);Array.Clear(expected,0,expected.Length);}
                if(diff==0){state.FailedAttempts=0;state.LockedUntilUtc=null;Save();Unlock(state.UnlockMinutes,attemptGeneration);return IsUnlocked;}
                state.FailedAttempts++;
                if(state.FailedAttempts>=5)state.LockedUntilUtc=clock().AddSeconds(Math.Min(900,30*Math.Pow(2,Math.Min(5,state.FailedAttempts-5)))).ToString("O");
                Interlocked.Exchange(ref unlockedUntilTicks,0);Save();return false;
            }
        }
        public void Lock(){Interlocked.Increment(ref generation);Interlocked.Exchange(ref unlockedUntilTicks,0);}
        public void Disable(){lock(gate){if(state!=null&&!IsUnlocked)throw new UnauthorizedAccessException("Unlock before removing protection.");var previous=state;state=null;try{Save();}catch{state=previous;throw;}configured=false;Lock();}}
    }
}
