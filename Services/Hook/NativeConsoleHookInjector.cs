using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WreckfestController.Services.Hook;

internal static class NativeConsoleHookInjector
{
    private const string InitializeExportName = "WreckfestConsoleHookInitialize";
    private const string ShutdownExportName = "WreckfestConsoleHookShutdown";
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
    private const uint ErrorShutdownInProgress = 1115;
    private const int ErrorNoMoreFiles = 18;
    private const uint Infinite = 0xFFFFFFFF;
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
            if (!TryFindRemoteModule(processId, Path.GetFileName(dllPath), out var existing, out error))
            {
                return false;
            }

            if (existing is { } loaded)
            {
                wasAlreadyLoaded = true;
                if (CallRemoteExport(processHandle, loaded.Base, InitializeExportName, timeout, out var initialized, out error))
                {
                    return true;
                }

                // An unload whose shutdown timed out leaves the hook shut down partway, and
                // it refuses to start again. Finish taking it out, then load it afresh.
                if (initialized != ErrorShutdownInProgress)
                {
                    return false;
                }

                if (!UnloadLoaded(processHandle, processId, dllPath, loaded, out error))
                {
                    error = $"The hook in process {processId} is shut down and could not be unloaded to load it again: {error}";
                    return false;
                }

                wasAlreadyLoaded = false;
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

            // A fresh mapping: whatever was recorded for an earlier one at this address is
            // not about this one.
            ForgetReleasedReference(processHandle, processId, loadedModuleBase);

            return CallRemoteExport(
                processHandle,
                loadedModuleBase,
                InitializeExportName,
                timeout,
                out _,
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

    /// <summary>
    /// Takes the hook out of a process the controller is letting go of: its shutdown
    /// export stops the hook's threads, removes its patches from the game and gives back
    /// the hook's own module reference, then a remote FreeLibrary gives back the one
    /// LoadLibraryW took at injection, so the module is unmapped. Without that second
    /// step the shut-down module would stay loaded, and a later injection would find it
    /// and be refused by it. True when no hook is loaded.
    /// </summary>
    /// <remarks>
    /// Waits for each remote thread to end, however long that takes - it ends with the
    /// process at the latest. Returning while one still ran would let an injection
    /// resolve the module just before that FreeLibrary unmapped it.
    /// </remarks>
    public static bool UnloadDll(int processId, string dllPath, out string error)
    {
        error = string.Empty;

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

        try
        {
            if (!TryFindRemoteModule(processId, Path.GetFileName(dllPath), out var module, out error))
            {
                return false;
            }

            return module is not { } loaded || UnloadLoaded(processHandle, processId, dllPath, loaded, out error);
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    /// <remarks>
    /// Nothing is freed when the shutdown fails: the hook then keeps itself pinned, so it
    /// cannot be unmapped under its own threads, and a later call retries. Exactly one
    /// reference is given back - the controller's injection takes one, a reinjection into
    /// a loaded hook none - so a reference anything else holds is left alone.
    /// </remarks>
    private static bool UnloadLoaded(IntPtr processHandle, int processId, string dllPath, (IntPtr Base, string Path) module, out string error)
    {
        if (!string.Equals(Path.GetFullPath(module.Path), Path.GetFullPath(dllPath), StringComparison.OrdinalIgnoreCase))
        {
            error = $"The hook loaded in process {processId} is {module.Path}, not {dllPath}; left loaded";
            return false;
        }

        if (!CallRemoteExport(processHandle, module.Base, ShutdownExportName, Timeout.InfiniteTimeSpan, out _, out error))
        {
            return false;
        }

        // Given back already, by an earlier unload that found the module still loaded
        // afterwards: what keeps it loaded is not the controller's to release.
        var reference = ReferenceKey(processHandle, processId, module.Base);
        bool releasedBefore;
        lock (ReleasedReferences)
        {
            releasedBefore = reference is { } key && ReleasedReferences.Contains(key);
        }

        if (!releasedBefore)
        {
            var freeLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "FreeLibrary");
            if (freeLibrary == IntPtr.Zero)
            {
                error = $"Could not resolve FreeLibrary: {FormatLastWin32Error()}";
                return false;
            }

            if (!RunRemoteThread(processHandle, freeLibrary, module.Base, Timeout.InfiniteTimeSpan, "FreeLibrary", out var freed, out error))
            {
                return false;
            }

            if (freed == 0)
            {
                error = "Remote FreeLibrary failed";
                return false;
            }

            // Recorded at once: the snapshot below can fail, and a retry must still know.
            if (reference is { } released)
            {
                lock (ReleasedReferences)
                {
                    ReleasedReferences.Add(released);
                }
            }
        }

        if (!TryFindRemoteModule(processId, Path.GetFileName(dllPath), out var still, out error))
        {
            return false;
        }

        if (still is { } remaining && remaining.Base == module.Base)
        {
            error = "The hook is shut down but still loaded: something else holds a reference to it";
            return false;
        }

        // Unmapped: a later load may land at the same address, and is a new reference.
        ForgetReleasedReference(processHandle, processId, module.Base);
        return true;
    }

    /// <summary>
    /// Mappings of the hook whose controller reference has been given back and that have
    /// not been seen unmapped since, by process (its id and creation time, as a PID is reused)
    /// and base address. Kept for this run of the controller only.
    /// </summary>
    private static readonly HashSet<(int ProcessId, long Created, IntPtr Base)> ReleasedReferences = [];

    private static (int, long, IntPtr)? ReferenceKey(IntPtr processHandle, int processId, IntPtr moduleBase) =>
        GetProcessTimes(processHandle, out var created, out _, out _, out _) ? (processId, created, moduleBase) : null;

    private static void ForgetReleasedReference(IntPtr processHandle, int processId, IntPtr moduleBase)
    {
        if (ReferenceKey(processHandle, processId, moduleBase) is { } key)
        {
            lock (ReleasedReferences)
            {
                ReleasedReferences.Remove(key);
            }
        }
    }

    private static bool CallRemoteExport(
        IntPtr processHandle,
        IntPtr remoteModuleBase,
        string exportName,
        TimeSpan timeout,
        out uint exitCode,
        out string error)
    {
        exitCode = 0;

        // From the loaded image's own export table, not the DLL file's: the file can have
        // been renamed while loaded and another build put in its place.
        if (!TryResolveRemoteExport(processHandle, remoteModuleBase, exportName, out var remoteExport, out error))
        {
            error = $"Could not resolve {exportName} in the loaded hook: {error}";
            return false;
        }

        if (!RunRemoteThread(processHandle, remoteExport, IntPtr.Zero, timeout, exportName, out exitCode, out error))
        {
            return false;
        }

        if (exitCode != 0)
        {
            error = $"Remote {exportName} returned error {exitCode}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The address of <paramref name="exportName"/> in the 64-bit image loaded at
    /// <paramref name="moduleBase"/>, read from that image's export directory.
    /// </summary>
    internal static bool TryResolveRemoteExport(IntPtr processHandle, IntPtr moduleBase, string exportName, out IntPtr address, out string error)
    {
        address = IntPtr.Zero;

        var headers = new byte[0x400];
        if (!ReadRemote(processHandle, moduleBase, 0, headers, out error))
        {
            return false;
        }

        var pe = BitConverter.ToInt32(headers, 0x3C);
        var optional = pe + 24;
        // The export directory is the first data directory, 112 bytes into a PE32+ optional header.
        if (BitConverter.ToUInt16(headers, 0) != 0x5A4D || pe < 0 || optional + 120 > headers.Length ||
            BitConverter.ToUInt32(headers, pe) != 0x00004550 || BitConverter.ToUInt16(headers, optional) != 0x20B)
        {
            error = "not a 64-bit PE image";
            return false;
        }

        var exportRva = BitConverter.ToUInt32(headers, optional + 112);
        var exportSize = BitConverter.ToUInt32(headers, optional + 116);
        var directory = new byte[40];
        if (exportRva == 0 || !ReadRemote(processHandle, moduleBase, exportRva, directory, out error))
        {
            error = exportRva == 0 ? "the image has no exports" : error;
            return false;
        }

        var nameCount = BitConverter.ToUInt32(directory, 24);
        var functionsRva = BitConverter.ToUInt32(directory, 28);
        var namesRva = BitConverter.ToUInt32(directory, 32);
        var ordinalsRva = BitConverter.ToUInt32(directory, 36);
        if (nameCount > 4096)
        {
            error = $"implausible export count {nameCount}";
            return false;
        }

        var names = new byte[nameCount * 4];
        var ordinals = new byte[nameCount * 2];
        if (!ReadRemote(processHandle, moduleBase, namesRva, names, out error) ||
            !ReadRemote(processHandle, moduleBase, ordinalsRva, ordinals, out error))
        {
            return false;
        }

        // The name and its terminator, compared byte for byte.
        var wanted = Encoding.ASCII.GetBytes(exportName + "\0");
        var candidate = new byte[wanted.Length];
        for (var index = 0; index < nameCount; index++)
        {
            if (!ReadRemote(processHandle, moduleBase, BitConverter.ToUInt32(names, index * 4), candidate, out error))
            {
                return false;
            }

            if (!candidate.AsSpan().SequenceEqual(wanted))
            {
                continue;
            }

            var ordinal = BitConverter.ToUInt16(ordinals, index * 2);
            var function = new byte[4];
            if (!ReadRemote(processHandle, moduleBase, functionsRva + ordinal * 4u, function, out error))
            {
                return false;
            }

            var functionRva = BitConverter.ToUInt32(function, 0);
            if (functionRva >= exportRva && functionRva < exportRva + exportSize)
            {
                error = $"{exportName} is forwarded to another module";
                return false;
            }

            address = IntPtr.Add(moduleBase, checked((int)functionRva));
            return true;
        }

        error = $"{exportName} is not exported";
        return false;
    }

    private static bool ReadRemote(IntPtr processHandle, IntPtr moduleBase, uint rva, byte[] buffer, out string error)
    {
        if (!ReadProcessMemory(processHandle, IntPtr.Add(moduleBase, checked((int)rva)), buffer, (UIntPtr)buffer.Length, out var read) ||
            read.ToUInt64() != (ulong)buffer.Length)
        {
            error = $"ReadProcessMemory failed: {FormatLastWin32Error()}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="start"/> on a new thread in the process and waits for its exit
    /// code; <see cref="Timeout.InfiniteTimeSpan"/> waits until it ends.
    /// </summary>
    private static bool RunRemoteThread(
        IntPtr processHandle,
        IntPtr start,
        IntPtr parameter,
        TimeSpan timeout,
        string what,
        out uint exitCode,
        out string error)
    {
        exitCode = 0;
        error = string.Empty;

        var threadHandle = CreateRemoteThread(
            processHandle,
            IntPtr.Zero,
            UIntPtr.Zero,
            start,
            parameter,
            0,
            IntPtr.Zero);

        if (threadHandle == IntPtr.Zero)
        {
            error = $"CreateRemoteThread for {what} failed: {FormatLastWin32Error()}";
            return false;
        }

        try
        {
            var waitMs = timeout == Timeout.InfiniteTimeSpan ? Infinite : (uint)timeout.TotalMilliseconds;
            var waitResult = WaitForSingleObject(threadHandle, waitMs);
            if (waitResult == WaitTimeout)
            {
                error = $"Timed out waiting for remote {what} to complete";
                return false;
            }

            if (waitResult != WaitObject0)
            {
                error = $"WaitForSingleObject for {what} failed with result 0x{waitResult:X}";
                return false;
            }

            if (!GetExitCodeThread(threadHandle, out exitCode))
            {
                error = $"GetExitCodeThread for {what} failed: {FormatLastWin32Error()}";
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(threadHandle);
        }
    }

    private static IntPtr FindRemoteModuleBase(int processId, string moduleName) =>
        TryFindRemoteModule(processId, moduleName, out var module, out _) && module is { } found ? found.Base : IntPtr.Zero;

    /// <summary>
    /// Looks the module up in the process. False when the module list could not be read,
    /// which says nothing about whether it is loaded; true with null when it is not.
    /// </summary>
    private static bool TryFindRemoteModule(int processId, string moduleName, out (IntPtr Base, string Path)? module, out string error)
    {
        module = null;
        error = string.Empty;

        var snapshot = CreateModuleSnapshot(processId);
        if (snapshot == InvalidHandleValue)
        {
            error = $"Could not list the modules of process {processId}: {FormatLastWin32Error()}";
            return false;
        }

        try
        {
            var entry = new ModuleEntry32
            {
                DwSize = (uint)Marshal.SizeOf<ModuleEntry32>()
            };

            if (!Module32First(snapshot, ref entry))
            {
                if (Marshal.GetLastWin32Error() == ErrorNoMoreFiles)
                {
                    return true;
                }

                error = $"Could not list the modules of process {processId}: {FormatLastWin32Error()}";
                return false;
            }

            do
            {
                if (string.Equals(entry.SzModule, moduleName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(entry.SzExePath), moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    module = (entry.ModBaseAddr, entry.SzExePath);
                    return true;
                }
            }
            while (Module32Next(snapshot, ref entry));

            if (Marshal.GetLastWin32Error() != ErrorNoMoreFiles)
            {
                error = $"Could not list the modules of process {processId}: {FormatLastWin32Error()}";
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    // A module snapshot fails with ERROR_BAD_LENGTH while the target's loader is
    // changing its module list - a process still starting, or one loading a library
    // at that moment - and the documented remedy is to retry. Without this a hook
    // that had just loaded could be reported missing.
    private static IntPtr CreateModuleSnapshot(int processId)
    {
        const int ErrorBadLength = 24;
        const int ErrorPartialCopy = 299;
        const int MaxAttempts = 40;

        for (var attempt = 1; ; attempt++)
        {
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapModule | Th32csSnapModule32, (uint)processId);
            var error = Marshal.GetLastWin32Error();
            if (snapshot != InvalidHandleValue ||
                attempt >= MaxAttempts ||
                (error != ErrorBadLength && error != ErrorPartialCopy))
            {
                return snapshot;
            }

            Thread.Sleep(25);
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
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        UIntPtr nSize,
        out UIntPtr lpNumberOfBytesRead);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

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
