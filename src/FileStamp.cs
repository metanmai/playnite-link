using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlaytimeRclone
{
    // A write time and size alone miss rewrites with a restored write time.
    // NTFS also exposes the file identity and the filesystem change time.
    internal struct FileStamp : IEquatable<FileStamp>
    {
        private uint volume;
        private ulong id;
        private long length, changed, written;
        public bool Equals(FileStamp other)
        {
            return volume==other.volume&&id==other.id&&length==other.length&&changed==other.changed&&written==other.written;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInfo
        {
            public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
            public uint Attributes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int infoClass,out BasicInfo info,uint size);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file,out HandleInfo info);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file,IntPtr name,uint nameSize,
            out uint serial,out uint componentSize,out uint flags,StringBuilder filesystem,uint filesystemSize);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        private static extern uint GetDriveTypeW(string path);
        public static bool TryRead(FileStream file,out FileStamp stamp)
        {
            stamp=default(FileStamp);
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT||
                file.Name.StartsWith(@"\\",StringComparison.Ordinal)||GetDriveTypeW(Path.GetPathRoot(file.Name))!=3)return false;
            var handle=file.SafeFileHandle;
            BasicInfo basic;HandleInfo identity;
            if(!GetFileInformationByHandleEx(handle,0,out basic,(uint)Marshal.SizeOf(typeof(BasicInfo)))||
                basic.ChangeTime==0||!GetFileInformationByHandle(handle,out identity))return false;
            var filesystem=new StringBuilder(16);uint serial,componentSize,flags;
            if(!GetVolumeInformationByHandleW(handle,IntPtr.Zero,0,out serial,out componentSize,out flags,filesystem,16)||
                filesystem.ToString()!="NTFS")return false;
            stamp=new FileStamp
            {
                volume=identity.VolumeSerial,id=((ulong)identity.IndexHigh<<32)|identity.IndexLow,
                length=file.Length,changed=basic.ChangeTime,written=basic.LastWriteTime
            };
            return stamp.id!=0;
        }
    }
}
