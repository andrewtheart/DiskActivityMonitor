#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Validate', 'Install', 'Data')]
    [string]$Profile,

    [string]$ErrorFile
)

$ErrorActionPreference = 'Stop'

if (-not ('DiskActivityMonitor.Security.DirectoryHandleSecurity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskActivityMonitor.Security
{
    public static class DirectoryHandleSecurity
    {
        private const uint ReadControl = 0x00020000;
        private const uint WriteDac = 0x00040000;
        private const uint WriteOwner = 0x00080000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint OwnerSecurityInformation = 0x00000001;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const uint TokenAdjustPrivileges = 0x00000020;
        private const uint TokenQuery = 0x00000008;
        private const uint SePrivilegeEnabled = 0x00000002;
        private const int ErrorNotAllAssigned = 1300;
        private const int SeFileObject = 1;
        private const int FileAttributeTagInfoClass = 9;
        private const uint SddlRevision1 = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileAttributeTagInfo information,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor,
            uint stringSdRevision,
            out IntPtr securityDescriptor,
            out uint securityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSecurityDescriptorOwner(
            IntPtr securityDescriptor,
            out IntPtr owner,
            [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSecurityDescriptorDacl(
            IntPtr securityDescriptor,
            [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
            out IntPtr dacl,
            [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint SetSecurityInfo(
            SafeFileHandle handle,
            int objectType,
            uint securityInformation,
            IntPtr owner,
            IntPtr group,
            IntPtr dacl,
            IntPtr sacl);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out SafeFileHandle tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(
            string systemName,
            string name,
            out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            SafeFileHandle tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        public static void Validate(string path)
        {
            using (SafeFileHandle handle = OpenDirectory(path, 0))
                ValidateDirectoryHandle(handle, path);
        }

        public static void Apply(string path, string sddl)
        {
            IntPtr securityDescriptor;
            uint securityDescriptorSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    sddl, SddlRevision1, out securityDescriptor, out securityDescriptorSize))
                throw Win32("Could not parse the directory security descriptor");

            try
            {
                IntPtr owner;
                bool ownerDefaulted;
                if (!GetSecurityDescriptorOwner(securityDescriptor, out owner, out ownerDefaulted))
                    throw Win32("Could not read the directory owner descriptor");

                bool daclPresent;
                IntPtr dacl;
                bool daclDefaulted;
                if (!GetSecurityDescriptorDacl(
                        securityDescriptor, out daclPresent, out dacl, out daclDefaulted) ||
                    !daclPresent || dacl == IntPtr.Zero)
                    throw Win32("Could not read the directory access descriptor");

                TryEnablePrivilege("SeTakeOwnershipPrivilege");
                TryEnablePrivilege("SeRestorePrivilege");

                using (SafeFileHandle ownerHandle = OpenDirectory(path, WriteOwner))
                {
                    ValidateDirectoryHandle(ownerHandle, path);
                    ByHandleFileInformation originalIdentity = GetIdentity(ownerHandle, path);
                    uint ownerResult = SetSecurityInfo(
                        ownerHandle, SeFileObject, OwnerSecurityInformation,
                        owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (ownerResult != 0)
                        throw new Win32Exception((int)ownerResult, "Could not assign trusted directory ownership");

                    using (SafeFileHandle daclHandle = OpenDirectory(path, ReadControl | WriteDac))
                    {
                        ValidateDirectoryHandle(daclHandle, path);
                        ByHandleFileInformation daclIdentity = GetIdentity(daclHandle, path);
                        if (!SameIdentity(originalIdentity, daclIdentity))
                            throw new InvalidOperationException("The directory changed while its security was being applied.");

                        uint daclResult = SetSecurityInfo(
                            daclHandle,
                            SeFileObject,
                            DaclSecurityInformation | ProtectedDaclSecurityInformation,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            dacl,
                            IntPtr.Zero);
                        if (daclResult != 0)
                            throw new Win32Exception((int)daclResult, "Could not apply the protected directory DACL");
                    }
                }
            }
            finally
            {
                LocalFree(securityDescriptor);
            }
        }

        private static SafeFileHandle OpenDirectory(string path, uint desiredAccess)
        {
            SafeFileHandle handle = CreateFile(
                path,
                desiredAccess,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Could not open directory without following reparse points: " + path);
            }
            return handle;
        }

        private static void ValidateDirectoryHandle(SafeFileHandle handle, string path)
        {
            FileAttributeTagInfo information;
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out information,
                    (uint)Marshal.SizeOf(typeof(FileAttributeTagInfo))))
                throw Win32("Could not inspect directory attributes: " + path);
            if ((information.FileAttributes & FileAttributeDirectory) == 0)
                throw new InvalidOperationException("The secured path is not a directory: " + path);
            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to secure a directory reparse point: " + path);
        }

        private static ByHandleFileInformation GetIdentity(SafeFileHandle handle, string path)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                throw Win32("Could not read directory identity: " + path);
            return information;
        }

        private static bool SameIdentity(
            ByHandleFileInformation left,
            ByHandleFileInformation right)
        {
            return left.VolumeSerialNumber == right.VolumeSerialNumber &&
                left.FileIndexHigh == right.FileIndexHigh &&
                left.FileIndexLow == right.FileIndexLow;
        }

        private static bool TryEnablePrivilege(string name)
        {
            SafeFileHandle token;
            if (!OpenProcessToken(
                    GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                throw Win32("Could not open the installer process token");

            using (token)
            {
                Luid luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                    throw Win32("Could not resolve privilege " + name);

                TokenPrivileges privileges = new TokenPrivileges();
                privileges.PrivilegeCount = 1;
                privileges.Luid = luid;
                privileges.Attributes = SePrivilegeEnabled;
                if (!AdjustTokenPrivileges(
                        token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                    throw Win32("Could not enable privilege " + name);
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotAllAssigned)
                    return false;
                return true;
            }
        }

        private static Win32Exception Win32(string message)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), message);
        }
    }
}
'@
}

try {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    switch ($Profile) {
        'Validate' {
            [DiskActivityMonitor.Security.DirectoryHandleSecurity]::Validate($resolvedPath)
        }
        'Install' {
            [DiskActivityMonitor.Security.DirectoryHandleSecurity]::Apply(
                $resolvedPath,
                'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)')
        }
        'Data' {
            [DiskActivityMonitor.Security.DirectoryHandleSecurity]::Apply(
                $resolvedPath,
                'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;GRGXGW;;;BU)(A;OICIIO;0x1301bf;;;BU)')
        }
    }
} catch {
    $details = [Collections.Generic.List[string]]::new()
    $exception = $_.Exception
    while ($null -ne $exception) {
        $detail = "$($exception.GetType().Name): $($exception.Message)"
        if ($exception -is [ComponentModel.Win32Exception]) {
            $nativeMessage = [ComponentModel.Win32Exception]::new($exception.NativeErrorCode).Message
            $detail += " (Win32 $($exception.NativeErrorCode): $nativeMessage)"
        }
        $details.Add($detail)
        $exception = $exception.InnerException
    }
    $detailText = $details -join [Environment]::NewLine
    if (-not [string]::IsNullOrWhiteSpace($ErrorFile)) {
        [IO.File]::WriteAllText(
            [IO.Path]::GetFullPath($ErrorFile),
            $detailText,
            [Text.UTF8Encoding]::new($false))
    }
    throw $detailText
}
