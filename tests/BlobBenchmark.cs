using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

internal static class BlobBenchmark
{
    [StructLayout(LayoutKind.Sequential)]struct Io {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessIoCounters(IntPtr process,out Io counters);
    static void Main(string[] args)
    {
        var root=args[0];Directory.CreateDirectory(root);var saves=Path.Combine(root,"saves");Directory.CreateDirectory(saves);
        using(var output=File.Create(Path.Combine(saves,"world.sav"))){var buffer=new byte[1024*1024];new Random(17).NextBytes(buffer);for(int i=0;i<64;i++)output.Write(buffer,0,buffer.Length);}
        var assembly=Assembly.LoadFrom(args[1]);var type=assembly.GetType("PlaytimeRclone.BlobStore");var store=Activator.CreateInstance(type,new object[]{Path.Combine(root,"blobs")});var capture=type.GetMethod("Capture");capture.Invoke(store,new object[]{saves});
        Io before,after;if(!GetProcessIoCounters(Process.GetCurrentProcess().Handle,out before))throw new Exception("No I/O counters");
        var watch=Stopwatch.StartNew();var first=JsonConvert.SerializeObject(capture.Invoke(store,new object[]{saves}));watch.Stop();
        if(!GetProcessIoCounters(Process.GetCurrentProcess().Handle,out after))throw new Exception("No I/O counters");
        var second=JsonConvert.SerializeObject(capture.Invoke(store,new object[]{saves}));
        Console.WriteLine(JsonConvert.SerializeObject(new{assembly=args[1],snapshotBytes=64L*1024*1024,warmMilliseconds=watch.ElapsedMilliseconds,warmWriteBytes=after.WriteBytes-before.WriteBytes,warmReadBytes=after.ReadBytes-before.ReadBytes,unchangedHash=first==second},Formatting.Indented));
    }
}
