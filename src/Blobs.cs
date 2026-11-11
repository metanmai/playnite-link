using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PlaytimeRclone
{
    public sealed class BlobReference
    {
        public string Hash;public long Length;
        public static BlobReference Parse(string value)
        {
            var parts=(value??"").Split(':');long length;
            if(parts.Length!=3||parts[0]!="blob"||!Regex.IsMatch(parts[1],"^[a-f0-9]{64}$")||!Int64.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out length)||length<0)
                throw new InvalidDataException("Invalid blob reference.");
            return new BlobReference{Hash=parts[1],Length=length};
        }
        public override string ToString(){return "blob:"+Hash+":"+Length.ToString(CultureInfo.InvariantCulture);}
    }
    public sealed class SaveCaptureCache
    {
        internal readonly Dictionary<string,Tuple<FileStamp,BlobReference>> Sources=
            new Dictionary<string,Tuple<FileStamp,BlobReference>>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,FileStamp> Verified=new Dictionary<string,FileStamp>(StringComparer.Ordinal);
        public int SourceReads {get;internal set;}
        public int BlobVerifications {get;internal set;}
    }
    public sealed class BlobStore
    {
        private static readonly object restoreGate=new object();
        // Test-only fault observer; production never installs one.
        internal static Action<string> RestoreBoundary=null;
        private static void Boundary(string name){var observer=RestoreBoundary;if(observer!=null)observer(name);}
        public static void RecoverRestore(string folder)
        {
            lock(restoreGate)
            {
                var root=SaveFiles.Root(folder);var journal=root+".pl-restore";
                SaveFiles.CheckAncestors(journal);
                if(!File.Exists(journal))return;
                if(new FileInfo(journal).Length>80)throw new IOException("Invalid save recovery journal; launch blocked to preserve saves.");
                var record=File.ReadAllText(journal).Split('|');
                if(record.Length!=2||!Regex.IsMatch(record[0],"^[a-f0-9]{32}$")||(record[1]!="0"&&record[1]!="1"))throw new IOException("Invalid save recovery journal; launch blocked to preserve saves.");
                var backup=root+".pl-backup-"+record[0];SaveFiles.CheckAncestors(backup);
                if(File.Exists(root))throw new IOException("Save recovery destination is a file; original backup was preserved.");
                if(!Directory.Exists(root)&&record[1]=="1")
                {
                    if(!Directory.Exists(backup))throw new IOException("Save recovery needs the original backup; launch blocked.");
                    Directory.Move(backup,root);
                }
                // An existing root is either the untouched original or the fully staged
                // replacement. Never overwrite it; retain all sibling backups/stages.
                File.Delete(journal);
            }
        }
        public readonly string DirectoryPath;
        public Action<IEnumerable<string>> Fetch;
        public Func<bool> CancelRequested;
        public BlobStore(string path){DirectoryPath=path;Directory.CreateDirectory(path);}
        public string PathFor(string hash)
        { if(!Regex.IsMatch(hash??"","^[a-f0-9]{64}$"))throw new InvalidDataException("Invalid blob hash.");return Path.Combine(DirectoryPath,hash+".blob"); }
        static string Hex(byte[] value){return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
        public BlobReference Put(Stream input) { return Put(input,null); }
        private BlobReference Put(Stream input,SaveCaptureCache cycle)
        {
            // Full content is still hashed on every capture. Seekable files can reuse
            // existing blobs without writing an identical temporary copy to disk.
            if(input.CanSeek)
            {
                var start=input.Position;long count=0;string existingHash;
                using(var sha=SHA256.Create())
                {
                    var buffer=new byte[131072];int read;
                    while((read=input.Read(buffer,0,buffer.Length))>0)
                    {if(CancelRequested!=null&&CancelRequested())throw new OperationCanceledException();sha.TransformBlock(buffer,0,read,null,0);count=checked(count+read);}
                    sha.TransformFinalBlock(new byte[0],0,0);existingHash=Hex(sha.Hash);
                }
                var existing=PathFor(existingHash);
                if(File.Exists(existing))
                {
                    var reference=new BlobReference{Hash=existingHash,Length=count};
                    VerifyCached(reference,cycle);
                    return reference;
                }
                input.Position=start;
            }
            var temp=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".tmp");long length=0;string hash;
            try
            {
                using(var sha=SHA256.Create())
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.SequentialScan))
                {
                    var buffer=new byte[131072];int read;
                    while((read=input.Read(buffer,0,buffer.Length))>0){if(CancelRequested!=null&&CancelRequested())throw new OperationCanceledException();output.Write(buffer,0,read);sha.TransformBlock(buffer,0,read,null,0);length=checked(length+read);}
                    sha.TransformFinalBlock(new byte[0],0,0);hash=Hex(sha.Hash);output.Flush(true);
                }
                var destination=PathFor(hash);
                if(File.Exists(destination))
                { VerifyCached(new BlobReference{Hash=hash,Length=length},cycle); }
                else {try{File.Move(temp,destination);}catch(IOException){if(!File.Exists(destination))throw;VerifyCached(new BlobReference{Hash=hash,Length=length},cycle);}}
                return new BlobReference{Hash=hash,Length=length};
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        private void VerifyCached(BlobReference reference,SaveCaptureCache cycle)
        {
            if(cycle==null){CopyVerified(reference.ToString(),Stream.Null);return;}
            var path=PathFor(reference.Hash);
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                FileStamp stamp,previous;
                var stamped=FileStamp.TryRead(file,out stamp);
                var key=reference.ToString();
                if(stamped&&cycle.Verified.TryGetValue(key,out previous)&&stamp.Equals(previous))return;
                cycle.BlobVerifications++;
                CopyVerified(key,Stream.Null);
                if(stamped)cycle.Verified[key]=stamp;
            }
        }
        internal BlobReference PutFile(string path,SaveCaptureCache cycle)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan))
            {
                if(CancelRequested!=null&&CancelRequested())throw new OperationCanceledException();
                FileStamp stamp=default(FileStamp);
                var stamped=cycle!=null&&FileStamp.TryRead(file,out stamp);
                Tuple<FileStamp,BlobReference> previous;
                if(stamped&&cycle.Sources.TryGetValue(path,out previous)&&stamp.Equals(previous.Item1)&&File.Exists(PathFor(previous.Item2.Hash)))
                {
                    VerifyCached(previous.Item2,cycle);
                    return previous.Item2;
                }
                if(cycle!=null)cycle.SourceReads++;
                var reference=Put(file,cycle);
                if(stamped)cycle.Sources[path]=Tuple.Create(stamp,reference);
                return reference;
            }
        }
        private long captureCount;
        public long CaptureCount{get{return System.Threading.Interlocked.Read(ref captureCount);}}
        public SortedDictionary<string,string> Capture(string folder,SaveCaptureCache cycle=null)
        {
            RecoverRestore(folder);
            System.Threading.Interlocked.Increment(ref captureCount);
            var root=SaveFiles.Root(folder);var data=new SortedDictionary<string,string>(StringComparer.Ordinal);
            if(Directory.Exists(root))
            {
                var pending=new Stack<string>();pending.Push(root);
                while(pending.Count>0)
                {
                    foreach(var path in Directory.GetFileSystemEntries(pending.Pop()))
                    {
                        var attrs=File.GetAttributes(path);
                        if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("Junctions/symlinks are not supported in save folders.");
                        if((attrs&FileAttributes.Directory)!=0){pending.Push(path);continue;}
                        data.Add(path.Substring(root.Length+1).Replace('\\','/'),PutFile(path,cycle).ToString());
                    }
                }
            }
            SaveFiles.Validate(data);return data;
        }
        public SortedDictionary<string,string> ImportLegacy(SortedDictionary<string,string> data)
        {
            var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in data)
            {if(pair.Value.StartsWith("blob:",StringComparison.Ordinal))result.Add(pair.Key,pair.Value);else using(var memory=new MemoryStream(Convert.FromBase64String(pair.Value)))result.Add(pair.Key,Put(memory).ToString());}
            return result;
        }
        public void Ensure(SortedDictionary<string,string> data)
        {
            SaveFiles.Validate(data);
            var refs=data.Values.Where(v=>v.StartsWith("blob:",StringComparison.Ordinal)).Select(BlobReference.Parse).GroupBy(r=>r.Hash).Select(g=>g.First()).ToList();
            var missing=refs.Where(r=>!File.Exists(PathFor(r.Hash))).Select(r=>r.Hash+".blob").ToList();
            if(missing.Count>0){if(Fetch==null)throw new IOException("Required save blobs have not been downloaded.");Fetch(missing);}
            foreach(var r in refs)if(!File.Exists(PathFor(r.Hash))||new FileInfo(PathFor(r.Hash)).Length!=r.Length)throw new IOException("Missing or truncated cloud save blob: "+r.Hash);
        }
        public void CopyVerified(string value,Stream destination)
        {
            if(!value.StartsWith("blob:",StringComparison.Ordinal)){var bytes=Convert.FromBase64String(value);destination.Write(bytes,0,bytes.Length);return;}
            var reference=BlobReference.Parse(value);
            VerifyFile(reference,PathFor(reference.Hash),destination);
        }
        private void VerifyFile(BlobReference reference,string path,Stream destination)
        {
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan))
            using(var sha=SHA256.Create())
            {
                if(input.Length!=reference.Length)throw BadBlob();
                var length=CopyBounded(input,destination,reference.Length,CancelRequested,(buffer,count)=>sha.TransformBlock(buffer,0,count,null,0));
                sha.TransformFinalBlock(new byte[0],0,0);
                if(length!=reference.Length||Hex(sha.Hash)!=reference.Hash)throw BadBlob();
            }
        }
        private static InvalidDataException BadBlob()
        {return new InvalidDataException("Cloud save blob failed length or SHA-256 verification. Original data was kept.");}

        // Read no more than one byte past the limit, and never write it. For
        // ordinary saves the limit is just the content-addressed file length.
        internal static long CopyBounded(Stream input,Stream destination,long limit,Func<bool> cancelled,Action<byte[],int> copied=null)
        {
            var buffer=new byte[131072];long length=0;
            while(true)
            {
                if(cancelled!=null&&cancelled())throw new OperationCanceledException();
                var remaining=limit-length;
                var count=input.Read(buffer,0,remaining<buffer.Length?(int)remaining+1:buffer.Length);
                if(count==0)return length;
                if(count>remaining)throw BadBlob();
                destination.Write(buffer,0,count);
                if(copied!=null)copied(buffer,count);
                length+=count;
            }
        }
        internal void FetchVerified(BlobReference reference,Action<BlobReference,Stream> fetch)
        {
            var destination=PathFor(reference.Hash);
            if(File.Exists(destination))return;
            if(fetch==null)throw new IOException("Required achievement customization data has not been downloaded.");
            var temporary=Path.Combine(DirectoryPath,Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {fetch(reference,output);output.Flush(true);}
                VerifyFile(reference,temporary,Stream.Null);
                try{File.Move(temporary,destination);}
                catch(IOException){if(!File.Exists(destination))throw;VerifyFile(reference,destination,Stream.Null);}
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        internal sealed class StagedFolder : IDisposable
        {
            readonly BlobStore store;
            readonly string root,id,stage,backup;
            internal StagedFolder(BlobStore store,string root)
            {
                this.store=store;this.root=root;id=Guid.NewGuid().ToString("N");
                stage=root+".pl-stage-"+id;backup=root+".pl-backup-"+id;
            }
            internal void Prepare(SortedDictionary<string,string> data)
            {
                Directory.CreateDirectory(stage);
                foreach(var pair in data)
                {
                    var path=Path.Combine(stage,pair.Key.Replace('/',Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.SequentialScan))
                    {store.CopyVerified(pair.Value,output);output.Flush(true);}
                }
                Boundary("staged");
            }
            internal string Commit(string expected,SaveCaptureCache cycle=null)
            {
                SaveFiles.CheckAncestors(root);
                if(Revision.ContentHashOf(store.Capture(root,cycle))!=expected)throw new IOException("Local saves changed while restore was staging.");
                lock(restoreGate)
                {
                    var journal=root+".pl-restore";SaveFiles.CheckAncestors(journal);
                    Atomic.Write(journal,id+"|"+(Directory.Exists(root)?"1":"0"));
                    Boundary("journal");
                    if(Directory.Exists(root))Directory.Move(root,backup);
                    Boundary("backup");
                    try{Directory.Move(stage,root);}catch{RecoverRestore(root);throw;}
                    Boundary("installed");
                    File.Delete(journal);
                    Boundary("committed");
                }
                return Directory.Exists(backup)?backup:null;
            }
            public void Dispose(){if(Directory.Exists(stage))Directory.Delete(stage,true);}
        }
        internal StagedFolder PrepareRestore(string folder,SortedDictionary<string,string> data)
        {
            var staged=new StagedFolder(this,SaveFiles.Root(folder));
            try{staged.Prepare(data);return staged;}catch{staged.Dispose();throw;}
        }
        public string Restore(string folder,SortedDictionary<string,string> data,string expected,Action beforeReplace=null)
        {
            Ensure(data);
            if(Revision.ContentHashOf(Capture(folder))!=expected)throw new IOException("Local saves changed during sync; restore deferred.");
            using(var staged=PrepareRestore(folder,data))
            {
                if(beforeReplace!=null)beforeReplace();
                return staged.Commit(expected);
            }
        }
        public byte[] ReadSmall(string reference)
        {using(var memory=new MemoryStream()){CopyVerified(reference,memory);return memory.ToArray();}}
    }
}
