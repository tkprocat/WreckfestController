#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
constexpr uintptr_t ConsolePrintRva = 0x00F1A050;
constexpr uintptr_t CommandDispatcherRva = 0x00F18B30;
constexpr uintptr_t RegistryLookupRva = 0x00E37140;
constexpr uintptr_t RegistryTablePtrRva = 0x0127E7F8;
constexpr uintptr_t ServerNamespaceTagRva = 0x065E6308;
constexpr SIZE_T PatchSize = 12;

using ConsolePrintFn = void(__fastcall*)(const char*, void*, void*, void*);
using CommandDispatcherFn = void(__fastcall*)(void*);
using RegistryLookupFn = int(__fastcall*)(const char*, uintptr_t);

CRITICAL_SECTION g_hookLock;
CRITICAL_SECTION g_outputLock;
CRITICAL_SECTION g_dispatchLock;
bool g_locksReady = false;
bool g_hookInstalled = false;
bool g_inputStarted = false;
BYTE g_originalBytes[PatchSize] = {};
void* g_target = nullptr;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
wchar_t g_fallbackLogPath[MAX_PATH] = {};

struct CommandTokens
{
    void* reserved0 = nullptr;
    void* reserved8 = nullptr;
    char* command = nullptr;
    char* argument = nullptr;
    void* reserved20 = nullptr;
};

bool InvokeDispatcherNoThrow(CommandDispatcherFn dispatcher, CommandTokens* tokens)
{
    __try
    {
        dispatcher(tokens);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

bool ReadPlayersNoThrow(std::string& response)
{
    __try
    {
        auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        auto lookup = reinterpret_cast<RegistryLookupFn>(moduleBase + RegistryLookupRva);
        auto registryTable = *reinterpret_cast<uintptr_t*>(moduleBase + RegistryTablePtrRva);
        auto serverNamespaceTag = *reinterpret_cast<uintptr_t*>(moduleBase + ServerNamespaceTagRva);

        if (registryTable == 0)
        {
            response = "ERR player snapshot registry table unavailable\n";
            return false;
        }

        int serverIndex = lookup("SERVER", serverNamespaceTag);
        if (serverIndex < 0)
        {
            response = "ERR player snapshot SERVER lookup failed\n";
            return false;
        }

        auto serverObject = *reinterpret_cast<uintptr_t*>(registryTable + static_cast<uintptr_t>(serverIndex) * 0x138 + 0x406040);
        if (serverObject == 0)
        {
            response = "ERR player snapshot SERVER object unavailable\n";
            return false;
        }

        auto playerTable = *reinterpret_cast<uintptr_t*>(serverObject + 0x30);
        if (playerTable == 0)
        {
            response = "ERR player snapshot player table unavailable\n";
            return false;
        }

        response.clear();
        response.reserve(2048);

        int count = 0;
        char line[512] = {};
        for (int slot = 0; slot < 24; slot++)
        {
            auto player = playerTable + static_cast<uintptr_t>(slot) * 0x138;
            auto status = *reinterpret_cast<unsigned char*>(player + 0xA6);
            if (status == 0)
            {
                continue;
            }

            auto flags = *reinterpret_cast<unsigned short*>(player + 0x82);
            auto ping = *reinterpret_cast<short*>(player + 0xA8);
            auto name = reinterpret_cast<const char*>(player + 0x48);
            if (name == nullptr || name[0] == '\0')
            {
                name = "<unknown>";
            }

            std::snprintf(
                line,
                sizeof(line),
                "PLAYER slot=%d status=%u flags=%u ping=%d name=%.*s\n",
                slot + 1,
                static_cast<unsigned int>(status),
                static_cast<unsigned int>(flags),
                static_cast<int>(ping),
                96,
                name);
            response += line;
            count++;
        }

        std::snprintf(line, sizeof(line), "OK players count=%d\n", count);
        response += line;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        response = "ERR player snapshot raised an exception\n";
        return false;
    }
}

void ClosePipe()
{
    EnterCriticalSection(&g_outputLock);
    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
    LeaveCriticalSection(&g_outputLock);
}

void WriteFallbackLog(const char* text)
{
    if (g_fallbackLogPath[0] == L'\0' || text == nullptr)
    {
        return;
    }

    HANDLE file = CreateFileW(
        g_fallbackLogPath,
        FILE_APPEND_DATA,
        FILE_SHARE_READ,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    DWORD written = 0;
    WriteFile(file, text, static_cast<DWORD>(std::strlen(text)), &written, nullptr);
    WriteFile(file, "\r\n", 2, &written, nullptr);
    CloseHandle(file);
}

void WriteHookLine(const char* text)
{
    if (text == nullptr || *text == '\0')
    {
        return;
    }

    EnterCriticalSection(&g_outputLock);

    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        DWORD written = 0;
        BOOL wroteText = WriteFile(g_pipe, text, static_cast<DWORD>(std::strlen(text)), &written, nullptr);
        BOOL wroteNewline = WriteFile(g_pipe, "\n", 1, &written, nullptr);
        if (wroteText && wroteNewline)
        {
            FlushFileBuffers(g_pipe);
        }
        else
        {
            CloseHandle(g_pipe);
            g_pipe = INVALID_HANDLE_VALUE;
        }
    }

    WriteFallbackLog(text);
    LeaveCriticalSection(&g_outputLock);
}

bool SetPatchBytes(const BYTE* bytes)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(g_target, PatchSize, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        return false;
    }

    std::memcpy(g_target, bytes, PatchSize);
    FlushInstructionCache(GetCurrentProcess(), g_target, PatchSize);

    DWORD ignored = 0;
    VirtualProtect(g_target, PatchSize, oldProtect, &ignored);
    return true;
}

bool RestoreHook()
{
    return SetPatchBytes(g_originalBytes);
}

bool InstallHook();

void __fastcall HookedConsolePrint(const char* text, void* arg2, void* arg3, void* arg4)
{
    WriteHookLine(text);

    EnterCriticalSection(&g_hookLock);
    RestoreHook();

    auto original = reinterpret_cast<ConsolePrintFn>(g_target);
    original(text, arg2, arg3, arg4);

    InstallHook();
    LeaveCriticalSection(&g_hookLock);
}

bool InstallHook()
{
    BYTE patch[PatchSize] = {
        0x48, 0xB8,                         // mov rax, imm64
        0, 0, 0, 0, 0, 0, 0, 0,
        0xFF, 0xE0                          // jmp rax
    };

    auto hookAddress = reinterpret_cast<uintptr_t>(&HookedConsolePrint);
    std::memcpy(patch + 2, &hookAddress, sizeof(hookAddress));

    if (!SetPatchBytes(patch))
    {
        return false;
    }

    g_hookInstalled = true;
    return true;
}

void InitializeFallbackLogPath()
{
    wchar_t tempPath[MAX_PATH] = {};
    if (GetTempPathW(MAX_PATH, tempPath) == 0)
    {
        return;
    }

    swprintf_s(
        g_fallbackLogPath,
        MAX_PATH,
        L"%swreckfest_console_hook_%lu.log",
        tempPath,
        GetCurrentProcessId());
}

void ConnectPipe()
{
    wchar_t pipeName[128] = {};
    swprintf_s(
        pipeName,
        128,
        L"\\\\.\\pipe\\WreckfestConsoleHook-%lu",
        GetCurrentProcessId());

    ClosePipe();

    for (int attempt = 0; attempt < 50 && g_pipe == INVALID_HANDLE_VALUE; attempt++)
    {
        auto pipe = CreateFileW(
            pipeName,
            GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (pipe != INVALID_HANDLE_VALUE)
        {
            EnterCriticalSection(&g_outputLock);
            g_pipe = pipe;
            LeaveCriticalSection(&g_outputLock);
            WriteHookLine("WreckfestConsoleHook connected.");
            return;
        }

        WaitNamedPipeW(pipeName, 250);
        Sleep(100);
    }

    WriteFallbackLog("WreckfestConsoleHook could not connect to controller pipe.");
}

std::string TrimCommand(std::string command)
{
    while (!command.empty() && (command.back() == '\r' || command.back() == '\n' || command.back() == ' ' || command.back() == '\t'))
    {
        command.pop_back();
    }

    size_t first = 0;
    while (first < command.size() && (command[first] == ' ' || command[first] == '\t'))
    {
        first++;
    }

    return command.substr(first);
}

bool DispatchConsoleCommand(const std::string& rawCommand)
{
    auto commandLine = TrimCommand(rawCommand);
    if (commandLine.empty())
    {
        WriteHookLine("WreckfestConsoleHook input rejected empty command.");
        return false;
    }

    auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    auto dispatcher = reinterpret_cast<CommandDispatcherFn>(moduleBase + CommandDispatcherRva);

    auto split = commandLine.find_first_of(" \t");
    std::string command = split == std::string::npos ? commandLine : commandLine.substr(0, split);
    std::string argument;
    if (split != std::string::npos)
    {
        argument = TrimCommand(commandLine.substr(split + 1));
    }

    std::vector<char> commandBuffer(command.begin(), command.end());
    commandBuffer.push_back('\0');
    std::vector<char> argumentBuffer(argument.begin(), argument.end());
    argumentBuffer.push_back('\0');

    CommandTokens tokens;
    tokens.command = commandBuffer.data();
    tokens.argument = argumentBuffer.data();

    EnterCriticalSection(&g_dispatchLock);
    bool dispatched = InvokeDispatcherNoThrow(dispatcher, &tokens);
    LeaveCriticalSection(&g_dispatchLock);

    if (!dispatched)
    {
        WriteHookLine("WreckfestConsoleHook input dispatch raised an exception.");
        return false;
    }

    WriteHookLine(("WreckfestConsoleHook dispatched command: " + commandLine).c_str());
    return true;
}

std::string HandleInputCommand(const char* buffer)
{
    auto commandLine = TrimCommand(buffer == nullptr ? "" : buffer);
    if (commandLine == "__hook_players")
    {
        std::string response;
        ReadPlayersNoThrow(response);
        WriteHookLine("WreckfestConsoleHook read player snapshot.");
        return response;
    }

    bool dispatched = DispatchConsoleCommand(commandLine);
    return dispatched ? "OK dispatched\n" : "ERR dispatch failed\n";
}

DWORD WINAPI InputPipeThread(void*)
{
    InitializeFallbackLogPath();

    wchar_t pipeName[128] = {};
    swprintf_s(
        pipeName,
        128,
        L"\\\\.\\pipe\\WreckfestConsoleHookInput-%lu",
        GetCurrentProcessId());

    WriteHookLine("WreckfestConsoleHook input pipe starting.");

    while (true)
    {
        HANDLE pipe = CreateNamedPipeW(
            pipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            4096,
            4096,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            WriteFallbackLog("WreckfestConsoleHook input pipe CreateNamedPipeW failed.");
            Sleep(1000);
            continue;
        }

        BOOL connected = ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected)
        {
            char buffer[2048] = {};
            DWORD bytesRead = 0;
            if (ReadFile(pipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr) && bytesRead > 0)
            {
                buffer[bytesRead] = '\0';
                auto response = HandleInputCommand(buffer);
                DWORD written = 0;
                WriteFile(pipe, response.c_str(), static_cast<DWORD>(response.size()), &written, nullptr);
            }
            else
            {
                const char* response = "ERR read failed\n";
                DWORD written = 0;
                WriteFile(pipe, response, static_cast<DWORD>(std::strlen(response)), &written, nullptr);
            }
        }

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}

DWORD WINAPI HookThread(void*)
{
    InitializeFallbackLogPath();
    ConnectPipe();

    EnterCriticalSection(&g_hookLock);
    if (g_hookInstalled)
    {
        WriteHookLine("WreckfestConsoleHook hook already installed; output reconnected.");
        LeaveCriticalSection(&g_hookLock);
        return 0;
    }

    auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    g_target = reinterpret_cast<void*>(moduleBase + ConsolePrintRva);

    std::memcpy(g_originalBytes, g_target, PatchSize);

    if (InstallHook())
    {
        WriteHookLine("WreckfestConsoleHook installed console print hook.");
    }
    else
    {
        WriteHookLine("WreckfestConsoleHook failed to install console print hook.");
    }
    LeaveCriticalSection(&g_hookLock);

    return 0;
}
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookVersion()
{
    return 1;
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookReconnect()
{
    HANDLE thread = CreateThread(nullptr, 0, HookThread, nullptr, 0, nullptr);
    if (thread == nullptr)
    {
        return GetLastError();
    }

    CloseHandle(thread);
    return 0;
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookStartInput()
{
    EnterCriticalSection(&g_dispatchLock);
    if (g_inputStarted)
    {
        LeaveCriticalSection(&g_dispatchLock);
        return 0;
    }

    g_inputStarted = true;
    LeaveCriticalSection(&g_dispatchLock);

    HANDLE thread = CreateThread(nullptr, 0, InputPipeThread, nullptr, 0, nullptr);
    if (thread == nullptr)
    {
        EnterCriticalSection(&g_dispatchLock);
        g_inputStarted = false;
        LeaveCriticalSection(&g_dispatchLock);
        return GetLastError();
    }

    CloseHandle(thread);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        InitializeCriticalSection(&g_hookLock);
        InitializeCriticalSection(&g_outputLock);
        InitializeCriticalSection(&g_dispatchLock);
        g_locksReady = true;

        WreckfestConsoleHookReconnect();
        WreckfestConsoleHookStartInput();
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (g_hookInstalled && g_target != nullptr)
        {
            EnterCriticalSection(&g_hookLock);
            RestoreHook();
            LeaveCriticalSection(&g_hookLock);
        }

        if (g_pipe != INVALID_HANDLE_VALUE)
        {
            ClosePipe();
        }

        if (g_locksReady)
        {
            DeleteCriticalSection(&g_outputLock);
            DeleteCriticalSection(&g_hookLock);
            DeleteCriticalSection(&g_dispatchLock);
        }
    }

    return TRUE;
}
