param(
    [Parameter(Mandatory)][string]$FilePath,
    [ValidateSet('', '--effects-perf', '--panel-perf', '--workspace-probe', '--navigation-probe', '--transform-probe', '--logging-probe', '--lifetime-probe', '--watcher-probe', '--mcp-interop-probe', '--recovery-probe', '--stress-matrix', '--retention-probe', '--paint-perf', '--direct-outline-probe', '--project-open-probe', '--project-save-probe', '--draft-exit-probe', '--ai-image-probe', '--localization-probe', '--timeline-probe', '--layer-drag-perf')][string]$Argument = '',
    [switch]$Visible
)
$ErrorActionPreference = 'Stop'
$resolvedExe = (Resolve-Path -LiteralPath $FilePath).Path
if (!(Test-Path -LiteralPath $resolvedExe -PathType Leaf)) { throw 'App executable is missing.' }
if (!('CompositorToolLauncher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class CompositorToolLauncher {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct StartupInfo {
        public int cb; public string reserved, desktop, title;
        public int x,y,xSize,ySize,xChars,yChars,fill,flags;
        public short show,reserved2; public IntPtr reservedBytes,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo info; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process,thread; public int pid,tid; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length,StringBuilder name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static bool ParentHasIdentity() {
        uint length=0; int status=GetCurrentPackageFullName(ref length,null);
        if (status==15700) return false; // APPMODEL_ERROR_NO_PACKAGE
        if (status==122) return true; // ERROR_INSUFFICIENT_BUFFER
        throw new Win32Exception(status);
    }
    public static Process Start(string exe,string argument,bool visible) {
        IntPtr size=IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
        IntPtr list=Marshal.AllocHGlobal(size), value=Marshal.AllocHGlobal(4); bool initialized=false;
        try {
            if (!InitializeProcThreadAttributeList(list,1,0,ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized=true;
            // PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY with ENABLE_PROCESS_TREE prevents
            // the unpackaged app from inheriting the tool host's MSIX resource identity.
            // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
            Marshal.WriteInt32(value,1);
            if (!UpdateProcThreadAttribute(list,0,(IntPtr)0x20012,value,(IntPtr)4,IntPtr.Zero,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup=new StartupInfoEx(); startup.info.cb=Marshal.SizeOf<StartupInfoEx>();
            startup.info.flags=1; startup.info.show=(short)(visible ? 1 : 0); startup.attributes=list; // STARTF_USESHOWWINDOW
            ProcessInfo native;
            if (!CreateProcess(exe,new StringBuilder("\""+exe+"\" "+argument),IntPtr.Zero,IntPtr.Zero,false,0x80000,IntPtr.Zero,System.IO.Path.GetDirectoryName(exe),ref startup,out native)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var process=Process.GetProcessById(native.pid);
                // Acquire a managed handle while alive so ExitCode remains available after exit.
                _=process.Handle;
                return process;
            } finally { CloseHandle(native.thread); CloseHandle(native.process); }
        } finally { if (initialized) DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(value); }
    }
}
'@
}
if ([CompositorToolLauncher]::ParentHasIdentity()) {
    [CompositorToolLauncher]::Start($resolvedExe,$Argument,[bool]$Visible)
} else {
    $startArguments = @{ FilePath=$resolvedExe; WorkingDirectory=(Split-Path -Parent $resolvedExe); PassThru=$true; WindowStyle=$(if ($Visible) { 'Normal' } else { 'Hidden' }) }
    if ($Argument) { $startArguments.ArgumentList=$Argument }
    Start-Process @startArguments
}
