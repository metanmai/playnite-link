using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using PlaytimeRclone;
static class RuntimeTests
{
    static int Main(string[] args)
    {
        var root=args[0];Directory.CreateDirectory(root);int pass=0;
        var zip=Path.Combine(root,"fixture.zip");
        using(var file=File.Create(zip))using(var archive=new ZipArchive(file,ZipArchiveMode.Create))
        {
            using(var stream=archive.CreateEntry("official/rclone.exe").Open())stream.Write(new byte[2048],0,2048);
            using(var stream=archive.CreateEntry("../escaped.txt").Open())stream.WriteByte(1);
        }
        string hash;using(var sha=SHA256.Create())using(var file=File.OpenRead(zip))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        var bad=Path.Combine(root,"bad");bool rejected=false;try{RcloneRuntime.InstallVerified(zip,new string('0',64),"official",bad);}catch(InvalidDataException){rejected=true;}
        if(!rejected||Directory.Exists(bad))throw new Exception("Checksum rejection modified installation");pass++;Console.WriteLine("PASS corrupt download rejected before extraction");
        var destination=Path.Combine(root,"verified");RcloneRuntime.InstallVerified(zip,hash,"official",destination);
        if(!File.Exists(Path.Combine(destination,"rclone.exe"))||File.Exists(Path.Combine(root,"escaped.txt")))throw new Exception("Unsafe archive extraction");pass++;Console.WriteLine("PASS archive paths cannot escape destination");
        var exe=RcloneRuntime.Ensure(Path.Combine(root,"download"));
        var start=new ProcessStartInfo(exe,"version"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
        using(var process=Process.Start(start)){var output=process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0||!output.Contains("v"+RcloneRuntime.Version))throw new Exception("Downloaded runtime did not execute expected release");}pass++;Console.WriteLine("PASS official pinned runtime downloaded, verified and executed");
        var stamp=File.GetLastWriteTimeUtc(exe);if(RcloneRuntime.Ensure(Path.Combine(root,"download"))!=exe||File.GetLastWriteTimeUtc(exe)!=stamp)throw new Exception("Repeated setup downloaded runtime again");pass++;Console.WriteLine("PASS subsequent setup reuses installed runtime");
        Console.WriteLine("RESULT "+pass+" passed, 0 failed");return 0;
    }
}
