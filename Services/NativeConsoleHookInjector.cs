using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WreckfestController.Services;

internal static class NativeConsoleHookInjector
{
    private const string InitializeExportName = "WreckfestConsoleHookInitialize";
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVirtualMemoryOperation = 0x0008;
    private const uint ProcessVirtualMemoryWrite = 0x0020;
    private const uint ProcessVirtualMemoryRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint Th32csSnapModule = 0x00000008;
    private const uint Th32csSnapModule32 = 0x00000010;
    private const uint DontResolveDllReferences = 0x00000001;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool InjectDll(int processId, string dllPath, TimeSpan timeout, out string error, out bool wasAlreadyLoaded)
    {
        error = string.Empty;
        wasAlreadyLoaded = false;

        if (!File.Exists(dllPath))
        {
            error = $"Hook DLL not found: {dllPath}";
            return false;
        }

        var processHandle = OpenProcess(
            ProcessCreateThread |
            ProcessQueryInformation |
            ProcessVirtualMemoryOperation |
            ProcessVirtualMemoryWrite |
            ProcessVirtualMemoryRead,
            false,
            processId);

        if (processHandle == IntPtr.Zero)
        {
            error = $"OpenProcess failed: {FormatLastWin32Error()}";
            return false;
        }

        IntPtr remotePath = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            var existingModuleBase = FindRemoteModuleBase(processId, Path.GetFileName(dllPath));
            if (existingModuleBase != IntPtr.Zero)
            {
                wasAlreadyLoaded = true;
                return CallRemoteExport(
                    processHandle,
                    existingModuleBase,
                    dllPath,
                    InitializeExportName,
                    timeout,
                    out error);
            }

            var dllPathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remotePath = VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (UIntPtr)dllPathBytes.Length,
                MemCommit | MemReserve,
                PageReadWrite);

            if (remotePath == IntPtr.Zero)
            {
                error = $"VirtualAllocEx failed: {FormatLastWin32Error()}";
                return false;
            }

            if (!WriteProcessMemory(
                    processHandle,
                    remotePath,
                    dllPathBytes,
                    (UIntPtr)dllPathBytes.Length,
                    out var bytesWritten) ||
                bytesWritten.ToUInt64() != (ulong)dllPathBytes.Length)
            {
                error = $"WriteProcessMemory failed: {FormatLastWin32Error()}";
                return false;
            }

            var kernel32 = GetModuleHandle("kernel32.dll");
            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                error = $"Could not resolve LoadLibraryW: {FormatLastWin32Error()}";
                return false;
            }

            threadHandle = CreateRemoteThread(
                processHandle,
                IntPtr.Zero,
                UIntPtr.Zero,
                loadLibrary,
                remotePath,
                0,
                IntPtr.Zero);

            if (threadHandle == IntPtr.Zero)
            {
                error = $"CreateRemoteThread failed: {FormatLastWin32Error()}";
                return false;
            }

            var waitResult = WaitForSingleObject(threadHandle, (uint)timeout.TotalMilliseconds);
            if (waitResult == WaitTimeout)
            {
                error = "Timed out waiting for remote LoadLibraryW to complete";
                return false;
            }

            if (waitResult != WaitObject0)
            {
                error = $"WaitForSingleObject failed with result 0x{waitResult:X}";
                return false;
            }

            if (!GetExitCodeThread(threadHandle, out var exitCode))
            {
                error = $"GetExitCodeThread failed: {FormatLastWin32Error()}";
                return false;
            }

            if (exitCode == 0)
            {
                error = "Remote LoadLibraryW returned null";
                return false;
            }

            var loadedModuleBase = FindRemoteModuleBase(processId, Path.GetFileName(dllPath));
            if (loadedModuleBase == IntPtr.Zero)
            {
                error = "Could not find loaded hook module after LoadLibraryW";
                return false;
            }

            return CallRemoteExport(
                processHandle,
                loadedModuleBase,
                dllPath,
                InitializeExportName,
                timeout,
                out error);
        }
        finally
        {
            // LoadLibraryW reads its argument straight out of the remote allocation, so
            // the page can only be released once the remote thread is definitely gone.
            // After a timeout the thread is still running: leak the page rather than
            // pull the path out from under it and fault the game process.
            var threadHasExited = threadHandle == IntPtr.Zero ||
                                  WaitForSingleObject(threadHandle, 0) == WaitObject0;

            if (threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }

            if (remotePath != IntPtr.Zero && threadHasExited)
            {
                VirtualFreeEx(processHandle, remotePath, UIntPtr.Zero, MemRelease);
            }

            CloseHandle(processHandle);
        }
    }

    private static bool CallRemoteExport(
        IntPtr processHandle,
        IntPtr remoteModuleBase,
        string dllPath,
        string exportName,
        TimeSpan timeout,
        out string error)
    {
        error = string.Empty;

        var localModule = LoadLibraryEx(dllPath, IntPtr.Zero, DontResolveDllReferences);
        if (localModule == IntPtr.Zero)
        {
            error = $"LoadLibraryEx failed while resolving {exportName}: {FormatLastWin32Error()}";
            return false;
        }

        IntPtr threadHandle = IntPtr.Zero;
        try
        {
            var localExport = GetProcAddress(localModule, exportName);
            if (localExport == IntPtr.Zero)
            {
                error = $"Could not resolve {exportName}: {FormatLastWin32Error()}";
                return false;
            }

            var exportOffset = localExport.ToInt64() - localModule.ToInt64();
            var remoteExport = IntPtr.Add(remoteModuleBase, checked((int)exportOffset));
            threadHandle = CreateRemoteThread(
                processHandle,
                IntPtr.Zero,
                UIntPtr.Zero,
                remoteExport,
                IntPtr.Zero,
                0,
                IntPtr.Zero);

            if (threadHandle == IntPtr.Zero)
            {
                error = $"CreateRemoteThread for {exportName} failed: {FormatLastWin32Error()}";
                return false;
            }

            var waitResult = WaitForSingleObject(threadHandle, (uint)timeout.TotalMilliseconds);
            if (waitResult == WaitTimeout)
            {
                error = $"Timed out waiting for remote {exportName} to complete";
                return false;
            }

            if (waitResult != WaitObject0)
            {
                error = $"WaitForSingleObject for {exportName} failed with result 0x{waitResult:X}";
                return false;
            }

            if (!GetExitCodeThread(threadHandle, out var exitCode))
            {
                error = $"GetExitCodeThread for {exportName} failed: {FormatLastWin32Error()}";
                return false;
            }

            if (exitCode != 0)
            {
                error = $"Remote {exportName} returned error {exitCode}";
                return false;
            }

            return true;
        }
        finally
        {
            if (threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }

            FreeLibrary(localModule);
        }
    }

    private static IntPtr FindRemoteModuleBase(int processId, string moduleName)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapModule | Th32csSnapModule32, (uint)processId);
        if (snapshot == InvalidHandleValue)
        {
            return IntPtr.Zero;
        }

        try
        {
            var entry = new ModuleEntry32
            {
                DwSize = (uint)Marshal.SizeOf<ModuleEntry32>()
            };

            if (!Module32First(snapshot, ref entry))
            {
                return IntPtr.Zero;
            }

            do
            {
                if (string.Equals(entry.SzModule, moduleName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(entry.SzExePath), moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.ModBaseAddr;
                }
            }
            while (Module32Next(snapshot, ref entry));

            return IntPtr.Zero;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string FormatLastWin32Error()
    {
        var errorCode = Marshal.GetLastWin32Error();
        return $"{new Win32Exception(errorCode).Message} ({errorCode})";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        UIntPtr nSize,
        out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr hProcess,
        IntPtr lpThreadAttributes,
        UIntPtr dwStackSize,
        IntPtr lpStartAddress,
        IntPtr lpParameter,
        uint dwCreationFlags,
        IntPtr lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32First(IntPtr hSnapshot, ref ModuleEntry32 lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32Next(IntPtr hSnapshot, ref ModuleEntry32 lpme);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint DwSize;
        public uint Th32ModuleId;
        public uint Th32ProcessId;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr ModBaseAddr;
        public uint ModBaseSize;
        public IntPtr HModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExePath;
    }
}
