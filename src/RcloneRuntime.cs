using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace PlaytimeRclone
{
    public static class RcloneRuntime
    {
        // Pinned official release and published SHA-256 values: no mutable latest
        // binary is trusted, and archive paths never determine an output path.
        public const string Version="1.75.2";
        private static readonly object gate=new object();
        public static string Ensure(string directory)
        {
            lock(gate)
            {
                var tools=Path.Combine(directory,"Tools","rclone-"+Version);var exe=Path.Combine(tools,"rclone.exe");
                if(File.Exists(exe))return exe;
                Directory.CreateDirectory(tools);
                var architecture=Environment.Is64BitOperatingSystem?"amd64":"386";
                if(String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"),"ARM64",StringComparison.OrdinalIgnoreCase))architecture="arm64";
                var hash=architecture=="amd64"?"8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818":architecture=="386"?"904876ca77f99f29400dfbb41ff99c132f64f1cdfa50fe7dc7923683b252186d":"7eb9844c9e5b723b9ef28821891caa17f6d9d1a375c49365cbe04114cec8dcd9";
                var name="rclone-v"+Version+"-windows-"+architecture;
                var archive=Path.Combine(tools,Guid.NewGuid().ToString("N")+".download");
                try
                {
                    ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                    using(var client=new DownloadClient())client.DownloadFile("https://downloads.rclone.org/v"+Version+"/"+name+".zip",archive);
                    InstallVerified(archive,hash,name,tools);
                    return exe;
                }
                finally{if(File.Exists(archive))File.Delete(archive);}
            }
        }
        private sealed class DownloadClient:WebClient
        {
            protected override WebRequest GetWebRequest(Uri address){var request=base.GetWebRequest(address);request.Timeout=120000;return request;}
        }
        public static void InstallVerified(string archive,string expected,string prefix,string destination)
        {
            string actual;using(var hash=SHA256.Create())using(var file=File.OpenRead(archive))actual=BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant();
            if(actual!=expected)throw new InvalidDataException("The Rclone download failed its integrity check. Nothing was installed; retry your connection.");
            using(var file=File.OpenRead(archive))using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
            {
                var entry=zip.GetEntry(prefix+"/rclone.exe");
                if(entry==null||entry.Length<1024||entry.Length>256L*1024*1024)throw new InvalidDataException("Unsupported Rclone archive layout.");
                Directory.CreateDirectory(destination);var target=Path.Combine(destination,"rclone.exe");var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{using(var input=entry.Open())using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}File.Move(temp,target);}
                finally{if(File.Exists(temp))File.Delete(temp);}
                var license=zip.GetEntry(prefix+"/README.html");
                if(license!=null)using(var input=license.Open())using(var output=File.Create(Path.Combine(destination,"README-rclone.html")))input.CopyTo(output);
            }
        }
    }
}
