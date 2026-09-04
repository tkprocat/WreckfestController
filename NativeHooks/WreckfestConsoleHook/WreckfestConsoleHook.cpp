#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <string>
#include <vector>
#include <MinHook.h>

namespace
{
constexpr uintptr_t ConsolePrintRva = 0x00F1A050;
constexpr uintptr_t CommandDispatcherRva = 0x00F18B30;
constexpr uintptr_t RegistryLookupRva = 0x00E37140;
constexpr uintptr_t RegistryTablePtrRva = 0x0127E7F8;
constexpr uintptr_t ServerNamespaceTagRva = 0x065E6308;
// FUN_14038fc10(int ringIndex, char* text, void* serverObject): the unified input
// handler for the server console and for player chat. See docs/finding-rvas.md.
constexpr uintptr_t ChatHandlerRva = 0x0038FC10;

// Framing for structured records on the output pipe. Picked so a record can never
// be mistaken for a console line: DC2 opens, DC3 closes, US separates fields.
constexpr char RecordStart = '\x12';
constexpr char RecordEnd = '\x13';
constexpr char FieldSeparator = '\x1f';
constexpr size_t MaxChatNameLength = 96;
constexpr size_t MaxChatMessageLength = 256;
constexpr size_t MaxConsoleLineLength = 1024;

#define NLSTR "\n"

// Set to the target build's SizeOfImage to hard-pin this hook to one Wreckfest
// build. 0 means "log the value but do not enforce" - run once, read the
// reported size from the hook log, then pin it here.
constexpr DWORD ExpectedImageSize = 0;

enum class LayoutStatus : DWORD
{
    Unchecked = 0,
    Ok = 1,
    HeadersUnreadable = 2,
    RvaOutOfRange = 3,
    RvaNotExecutable = 4,
    ImageSizeMismatch = 5,
};

LayoutStatus g_layoutStatus = LayoutStatus::Unchecked;
DWORD g_observedImageSize = 0;

using ConsolePrintFn = void(__fastcall*)(const char*, void*, void*, void*);
using CommandDispatcherFn = void(__fastcall*)(void*);
using RegistryLookupFn = int(__fastcall*)(const char*, uintptr_t);

// The decompile shows three parameters. A fourth register argument is declared and
// forwarded anyway: on x64 __fastcall the first four arguments live in rcx/rdx/r8/r9,
// and forwarding r9 untouched costs nothing while protecting us if the real
// signature is wider than Ghidra rendered it. The return type is likewise unknown -
// uintptr_t passes rax through unchanged, so a void function is unharmed and a
// value-returning one keeps working.
using ChatHandlerFn = uintptr_t(__fastcall*)(uintptr_t, const char*, uintptr_t, uintptr_t);

// Output is queued and written by a dedicated thread. WriteHookLine is called
// from Wreckfest's own thread (via the hooked print), and a blocking pipe write
// there lets a slow or stopped controller stall the game server itself. Enqueue,
// return immediately, and drop the oldest lines if the consumer falls behind.
constexpr size_t OutputQueueCapacity = 2048;
std::deque<std::string> g_outputQueue;
CRITICAL_SECTION g_queueLock;
HANDLE g_queueEvent = nullptr;
bool g_writerStarted = false;
volatile LONG g_droppedLines = 0;

CRITICAL_SECTION g_hookLock;
CRITICAL_SECTION g_outputLock;
CRITICAL_SECTION g_dispatchLock;
bool g_locksReady = false;
bool g_hookInstalled = false;
bool g_inputStarted = false;
ConsolePrintFn g_originalConsolePrint = nullptr;
void* g_target = nullptr;
bool g_chatHookInstalled = false;
ChatHandlerFn g_chatOriginal = nullptr;
void* g_chatTarget = nullptr;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
wchar_t g_fallbackLogPath[MAX_PATH] = {};

// Set for the duration of one chat-handler call, on the calling thread only. The
// game formats and prints the chat line from inside that call, so the hooked
// ConsolePrint below runs nested on this same thread and can pair the console line
// it is given with the raw message text captured here. Thread-local rather than
// global because several game threads can be inside the handler at once.
struct PendingChat
{
    bool active = false;
    bool emitted = false;
    int ringIndex = -1;
    std::string rawText;
};

thread_local PendingChat t_pendingChat;

struct CommandTokens
{
    void* reserved0 = nullptr;
    void* reserved8 = nullptr;
    char* command = nullptr;
    char* argument = nullptr;
    void* reserved20 = nullptr;
};

IMAGE_NT_HEADERS* GetNtHeaders(uintptr_t moduleBase)
{
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(moduleBase);
    if (dos == nullptr || dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return nullptr;
    }

    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(moduleBase + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        return nullptr;
    }

    return nt;
}

// True when rva falls inside a section carrying every flag in requiredFlags.
bool RvaHasSectionFlags(IMAGE_NT_HEADERS* nt, uintptr_t rva, DWORD requiredFlags)
{
    auto section = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, section++)
    {
        auto start = static_cast<uintptr_t>(section->VirtualAddress);
        auto size = section->Misc.VirtualSize != 0
            ? section->Misc.VirtualSize
            : section->SizeOfRawData;

        if (rva >= start && rva < start + size)
        {
            return (section->Characteristics & requiredFlags) == requiredFlags;
        }
    }

    return false;
}

// Verifies every hardcoded RVA still lands somewhere sane before we call or
// read through it. Without this a patched Wreckfest turns each RVA into a wild
// pointer, and the SEH guards below cannot tell "wrong function" from "fine".
LayoutStatus ValidateModuleLayoutNoThrow(uintptr_t moduleBase)
{
    __try
    {
        auto nt = GetNtHeaders(moduleBase);
        if (nt == nullptr)
        {
            return LayoutStatus::HeadersUnreadable;
        }

        g_observedImageSize = nt->OptionalHeader.SizeOfImage;

        const uintptr_t codeRvas[] = { ConsolePrintRva, CommandDispatcherRva, RegistryLookupRva, ChatHandlerRva };
        const uintptr_t dataRvas[] = { RegistryTablePtrRva, ServerNamespaceTagRva };

        for (auto rva : codeRvas)
        {
            if (rva >= g_observedImageSize)
            {
                return LayoutStatus::RvaOutOfRange;
            }

            if (!RvaHasSectionFlags(nt, rva, IMAGE_SCN_MEM_EXECUTE))
            {
                return LayoutStatus::RvaNotExecutable;
            }
        }

        for (auto rva : dataRvas)
        {
            if (rva >= g_observedImageSize)
            {
                return LayoutStatus::RvaOutOfRange;
            }

            if (!RvaHasSectionFlags(nt, rva, IMAGE_SCN_MEM_READ))
            {
                return LayoutStatus::RvaOutOfRange;
            }
        }

        if (ExpectedImageSize != 0 && g_observedImageSize != ExpectedImageSize)
        {
            return LayoutStatus::ImageSizeMismatch;
        }

        return LayoutStatus::Ok;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return LayoutStatus::HeadersUnreadable;
    }
}

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
    if (g_layoutStatus != LayoutStatus::Ok)
    {
        response = "ERR player snapshot module layout not validated\n";
        return false;
    }

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

// Performs the actual I/O. Only ever called on the writer thread.
void WriteHookLineBlocking(const char* text)
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

DWORD WINAPI OutputWriterThread(void*)
{
    for (;;)
    {
        WaitForSingleObject(g_queueEvent, 250);

        for (;;)
        {
            std::string line;
            bool have = false;

            EnterCriticalSection(&g_queueLock);
            if (!g_outputQueue.empty())
            {
                line = std::move(g_outputQueue.front());
                g_outputQueue.pop_front();
                have = true;
            }
            LeaveCriticalSection(&g_queueLock);

            if (!have)
            {
                break;
            }

            WriteHookLineBlocking(line.c_str());
        }

        // Surface backpressure rather than losing it silently.
        LONG dropped = InterlockedExchange(&g_droppedLines, 0);
        if (dropped > 0)
        {
            char note[128] = {};
            std::snprintf(note, sizeof(note),
                "WreckfestConsoleHook dropped %ld output line(s): controller not keeping up.",
                static_cast<long>(dropped));
            WriteHookLineBlocking(note);
        }
    }
}

// Called from the game's thread. Must never block on I/O.
void WriteHookLine(const char* text)
{
    if (text == nullptr || *text == '\0')
    {
        return;
    }

    EnterCriticalSection(&g_queueLock);
    if (g_outputQueue.size() >= OutputQueueCapacity)
    {
        g_outputQueue.pop_front();
        InterlockedIncrement(&g_droppedLines);
    }
    g_outputQueue.emplace_back(text);
    LeaveCriticalSection(&g_queueLock);

    if (g_queueEvent != nullptr)
    {
        SetEvent(g_queueEvent);
    }
}

bool RestoreHook()
{
    // MinHook owns the patch. Disabling restores the entry point in one step.
    return MH_DisableHook(g_target) == MH_OK;
}

bool RestoreChatHook()
{
    return MH_DisableHook(g_chatTarget) == MH_OK;
}

bool InstallHook();
void TryEmitStructuredChat(const char* consoleLine);


void __fastcall HookedConsolePrint(const char* text, void* arg2, void* arg3, void* arg4)
{
    // Emitted before the plain text line so the controller sees the structured
    // record first. There is no text fallback to skip any more, but the controller
    // warns about a chat-shaped console line that arrived without a record, so the
    // record has to land first or every message trips that warning.
    if (t_pendingChat.active && !t_pendingChat.emitted)
    {
        TryEmitStructuredChat(text);
    }

    WriteHookLine(text);

    // The trampoline holds the relocated original prologue plus a jump back past
    // the patch, so the entry point is never rewritten while the game runs. There
    // is nothing to restore and nothing to re-patch, so no lock is needed here and
    // no other thread can observe a half-written instruction stream.
    if (g_originalConsolePrint != nullptr)
    {
        g_originalConsolePrint(text, arg2, arg3, arg4);
    }
}

bool InstallHook()
{
    if (MH_CreateHook(
            g_target,
            reinterpret_cast<LPVOID>(&HookedConsolePrint),
            reinterpret_cast<LPVOID*>(&g_originalConsolePrint)) != MH_OK)
    {
        return false;
    }

    if (MH_EnableHook(g_target) != MH_OK)
    {
        MH_RemoveHook(g_target);
        g_originalConsolePrint = nullptr;
        return false;
    }

    g_hookInstalled = true;
    return true;
}

bool InstallChatHook();

// Copies a C string out of game memory without trusting it. A short-lived pointer
// into a freed buffer would otherwise take the whole server down.
bool CopyGameStringNoThrow(const char* text, size_t maxLength, std::string& out)
{
    out.clear();

    __try
    {
        if (text == nullptr)
        {
            return false;
        }

        for (size_t i = 0; i < maxLength; i++)
        {
            char c = text[i];
            if (c == '\0')
            {
                break;
            }

            out.push_back(c);
        }

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        out.clear();
        return false;
    }
}

// Hooked at the function's entry rather than at the internal format call site.
// The entry is the only place a 12-byte prologue patch is the same shape as the
// ConsolePrint one, so it reuses the restore -> call original -> repatch discipline
// unchanged; patching mid-function would need an offset into the body that nobody
// has confirmed against a live build, and docs/finding-rvas.md is explicit that an
// unconfirmed offset must not be invented. The cost of entry is that the
// player-table index is not resolved yet - param_1 is the input-ring index, whose
// mapping to the player table is unresolved - so the sender's name is recovered by
// pairing with the console line the handler prints while we are still inside it.
// See TryEmitStructuredChat.
uintptr_t __fastcall HookedChatHandler(uintptr_t ringIndex, const char* text, uintptr_t serverObject, uintptr_t arg4)
{
    // Saved and restored rather than simply cleared, so a nested call (the console
    // path re-entering the handler) cannot lose the outer call's state.
    PendingChat saved = t_pendingChat;

    t_pendingChat.active = true;
    t_pendingChat.emitted = false;
    t_pendingChat.ringIndex = static_cast<int>(ringIndex);
    CopyGameStringNoThrow(text, MaxChatMessageLength, t_pendingChat.rawText);

    // The caps here are byte counts while the game limits chat by characters, so a
    // multi-byte message can fill the buffer and be cut mid-sequence. Filling it
    // exactly is the signal; log it rather than guess whether it happens in practice.
    if (t_pendingChat.rawText.size() >= MaxChatMessageLength)
    {
        std::string warn = "WreckfestConsoleHook chat capture hit the ";
        warn += std::to_string(MaxChatMessageLength);
        warn += " byte cap and may be truncated mid-character";
        WriteHookLine(warn.c_str());
    }

    EnterCriticalSection(&g_hookLock);
    // Trampoline: the chat handler's entry point stays patched for the life of the
    // hook, so nothing here rewrites live code and no other game thread can execute
    // a half-written instruction stream.
    auto original = g_chatOriginal;
    uintptr_t result = original(ringIndex, text, serverObject, arg4);

    LeaveCriticalSection(&g_hookLock);

    t_pendingChat = saved;
    return result;
}

bool InstallChatHook()
{
    if (MH_CreateHook(
            g_chatTarget,
            reinterpret_cast<LPVOID>(&HookedChatHandler),
            reinterpret_cast<LPVOID*>(&g_chatOriginal)) != MH_OK)
    {
        return false;
    }

    if (MH_EnableHook(g_chatTarget) != MH_OK)
    {
        MH_RemoveHook(g_chatTarget);
        g_chatOriginal = nullptr;
        return false;
    }

    g_chatHookInstalled = true;
    return true;
}

// Control bytes would break the record framing, and the pipe is line-delimited, so
// anything below space becomes '?' rather than being dropped: a mangled character
// is visible, a silently shortened message is not.
std::string SanitizeRecordField(const std::string& value, size_t maxLength)
{
    std::string sanitized;
    sanitized.reserve(value.size() < maxLength ? value.size() : maxLength);

    for (size_t i = 0; i < value.size() && sanitized.size() < maxLength; i++)
    {
        auto c = static_cast<unsigned char>(value[i]);
        sanitized.push_back(c < 0x20 || c == 0x7F ? '?' : value[i]);
    }

    return sanitized;
}


// Pairs the message captured at entry with the line the game formatted from it, and
// ships both. The pairing is a containment test and nothing more: recovering the
// sender needs the "^8"/"^0" markers and the ": " separator, and that belongs in
// HookChatRecord where it is unit tested and cannot take the game process with it.
void TryEmitStructuredChat(const char* consoleLine)
{
    std::string line;
    if (!CopyGameStringNoThrow(consoleLine, MaxConsoleLineLength, line))
    {
        return;
    }

    const std::string& raw = t_pendingChat.rawText;

    // Matching only - the payload below stays verbatim. The ring is newline
    // delimited, so the message the handler received still carries its terminator
    // while the formatted line does not.
    std::string probe = raw;
    while (!probe.empty() &&
           (probe.back() == 10 || probe.back() == 13 || probe.back() == 32))
    {
        probe.pop_back();
    }

    if (probe.empty() || line.find(probe) == std::string::npos)
    {
        // Not the formatted line for this message. The handler prints other things,
        // so stay armed for the line that does carry it.
        //
        // Reported only when the message contains bytes above ASCII, because that is
        // the case we do not yet understand: whether the game hands us UTF-8, and
        // whether a truncated capture stops the two ever matching. Logging every
        // non-matching line would drown the console.
        bool nonAscii = false;
        for (size_t i = 0; i < probe.size(); i++)
        {
            if (static_cast<unsigned char>(probe[i]) >= 0x80)
            {
                nonAscii = true;
                break;
            }
        }

        if (nonAscii)
        {
            std::string warn = "WreckfestConsoleHook chat pairing failed for a non-ASCII message: probeBytes=";
            warn += std::to_string(probe.size());
            warn += " lineBytes=";
            warn += std::to_string(line.size());
            warn += " probe=[";
            warn += probe;
            warn += "]";
            WriteHookLine(warn.c_str());
        }

        return;
    }

    // Deliberately no interpretation here. Everything this needs to say is
    // something the hook actually observed: which ring entry, the message exactly
    // as the handler received it, and the line the game formatted from it. Working
    // out the sender means reasoning about "^8", "^0" and the ": " separator, and
    // that reasoning belongs somewhere it can be unit tested and where a mistake
    // costs a dropped command rather than a dead game process.
    std::string record;
    record.reserve(raw.size() + line.size() + 32);
    record.push_back(RecordStart);
    record += "CHAT";
    record.push_back(FieldSeparator);

    char indexText[16] = {};
    std::snprintf(indexText, sizeof(indexText), "%d", t_pendingChat.ringIndex);
    record += indexText;
    record.push_back(FieldSeparator);
    // The console line goes first so the message can be last: the message is what a
    // player types, so it must be the field a stray separator byte cannot truncate.
    record += SanitizeRecordField(line, MaxConsoleLineLength);
    record.push_back(FieldSeparator);
    record += SanitizeRecordField(raw, MaxChatMessageLength);
    record.push_back(RecordEnd);

    t_pendingChat.emitted = true;
    WriteHookLine(record.c_str());
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

bool DispatchConsoleCommand(const std::string& rawCommand, std::string* tokenEcho)
{
    auto commandLine = TrimCommand(rawCommand);
    if (commandLine.empty())
    {
        WriteHookLine("WreckfestConsoleHook input rejected empty command.");
        return false;
    }

    if (g_layoutStatus != LayoutStatus::Ok)
    {
        WriteHookLine("WreckfestConsoleHook refused dispatch: module layout not validated.");
        return false;
    }

    auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    auto dispatcher = reinterpret_cast<CommandDispatcherFn>(moduleBase + CommandDispatcherRva);

    auto split = commandLine.find_first_of(" \t=");
    std::string command = split == std::string::npos ? commandLine : commandLine.substr(0, split);
    std::string argument;
    if (split != std::string::npos)
    {
        auto argumentStart = split + 1;
        while (argumentStart < commandLine.size() &&
               (commandLine[argumentStart] == ' ' || commandLine[argumentStart] == '\t' || commandLine[argumentStart] == '='))
        {
            argumentStart++;
        }

        argument = TrimCommand(commandLine.substr(argumentStart));
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

    if (tokenEcho != nullptr)
    {
        *tokenEcho = "command=" + command + " argument=" + argument;
    }

    WriteHookLine(("WreckfestConsoleHook dispatched command: " + commandLine).c_str());
    return true;
}

// Reads module-relative memory for investigation. Deliberately RVA-only and
// bounded by SizeOfImage: an absolute address would let a typo read anywhere in
// the process, and this runs inside a live game server. Read-only by design -
// there is no write counterpart.
bool ReadModuleMemoryNoThrow(uintptr_t rva, size_t size, std::string& response)
{
    if (g_layoutStatus != LayoutStatus::Ok)
    {
        response = "ERR read module layout not validated" NLSTR;
        return false;
    }

    if (size == 0 || size > 1024)
    {
        response = "ERR read size must be 1..1024" NLSTR;
        return false;
    }

    if (g_observedImageSize == 0 || rva >= g_observedImageSize ||
        rva + size > g_observedImageSize)
    {
        response = "ERR read out of module bounds" NLSTR;
        return false;
    }

    __try
    {
        auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        auto p = reinterpret_cast<const unsigned char*>(moduleBase + rva);

        char head[96] = {};
        std::snprintf(head, sizeof(head), "OK read rva=0x%08llX size=%llu data=",
            static_cast<unsigned long long>(rva),
            static_cast<unsigned long long>(size));

        response = head;
        response.reserve(response.size() + size * 2 + 2);

        char byteText[3] = {};
        for (size_t i = 0; i < size; i++)
        {
            std::snprintf(byteText, sizeof(byteText), "%02x", p[i]);
            response += byteText;
        }
        response += NLSTR;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        response = "ERR read raised an exception" NLSTR;
        return false;
    }
}

std::string HandleInputCommand(const char* buffer)
{
    auto commandLine = TrimCommand(buffer == nullptr ? "" : buffer);

    // Hook-only commands are handled here and must never reach the game's
    // dispatcher.
    if (commandLine.rfind("__hook_read", 0) == 0)
    {
        unsigned long long rva = 0;
        unsigned long long size = 0;
        if (std::sscanf(commandLine.c_str(), "__hook_read %llx %llu", &rva, &size) != 2)
        {
            return "ERR read usage: __hook_read <rvaHex> <size>" NLSTR;
        }

        std::string response;
        ReadModuleMemoryNoThrow(static_cast<uintptr_t>(rva), static_cast<size_t>(size), response);
        return response;
    }

    if (commandLine == "__hook_info")
    {
        char info[160] = {};
        std::snprintf(info, sizeof(info),
            "OK info base=0x%llX imageSize=0x%08lX layout=%lu" NLSTR,
            static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr))),
            static_cast<unsigned long>(g_observedImageSize),
            static_cast<unsigned long>(g_layoutStatus));
        return info;
    }

    if (commandLine == "__hook_players")
    {
        std::string response;
        ReadPlayersNoThrow(response);
        WriteHookLine("WreckfestConsoleHook read player snapshot.");
        return response;
    }

    std::string tokenEcho;
    bool dispatched = DispatchConsoleCommand(commandLine, &tokenEcho);
    if (!dispatched)
    {
        return "ERR dispatch failed\n";
    }

    return "OK dispatched " + tokenEcho + "\n";
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

    g_layoutStatus = ValidateModuleLayoutNoThrow(moduleBase);
    {
        char line[256] = {};
        std::snprintf(
            line,
            sizeof(line),
            "WreckfestConsoleHook module layout status=%lu imageSize=0x%08lX expected=0x%08lX",
            static_cast<unsigned long>(g_layoutStatus),
            static_cast<unsigned long>(g_observedImageSize),
            static_cast<unsigned long>(ExpectedImageSize));
        WriteHookLine(line);
    }

    if (g_layoutStatus != LayoutStatus::Ok)
    {
        WriteHookLine("WreckfestConsoleHook aborted: offsets do not match this Wreckfest build.");
        LeaveCriticalSection(&g_hookLock);
        return 0;
    }

    g_target = reinterpret_cast<void*>(moduleBase + ConsolePrintRva);

    if (MH_Initialize() != MH_OK)
    {
        WriteHookLine("WreckfestConsoleHook failed to initialize MinHook.");
        LeaveCriticalSection(&g_hookLock);
        return 0;
    }

    if (InstallHook())
    {
        WriteHookLine("WreckfestConsoleHook installed console print hook.");
    }
    else
    {
        WriteHookLine("WreckfestConsoleHook failed to install console print hook.");
    }

    // Structured chat is additive: if this fails the controller simply never sees a
    // CHAT record and keeps parsing console text, so it must not abort the install.
    g_chatTarget = reinterpret_cast<void*>(moduleBase + ChatHandlerRva);

    if (InstallChatHook())
    {
        WriteHookLine("WreckfestConsoleHook installed chat handler hook.");
    }
    else
    {
        WriteHookLine("WreckfestConsoleHook failed to install chat handler hook; chat stays on console text.");
        g_chatTarget = nullptr;
    }
    LeaveCriticalSection(&g_hookLock);

    return 0;
}
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookVersion()
{
    return 2;
}

// 1 == Ok. Anything else means the hardcoded offsets did not validate against
// the running Wreckfest build; see LayoutStatus for the codes.
extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookLayoutStatus()
{
    return static_cast<DWORD>(g_layoutStatus);
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookImageSize()
{
    return g_observedImageSize;
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

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookStartOutputWriter()
{
    EnterCriticalSection(&g_queueLock);
    bool alreadyStarted = g_writerStarted;
    g_writerStarted = true;
    LeaveCriticalSection(&g_queueLock);

    if (alreadyStarted)
    {
        return 0;
    }

    HANDLE thread = CreateThread(nullptr, 0, OutputWriterThread, nullptr, 0, nullptr);
    if (thread == nullptr)
    {
        EnterCriticalSection(&g_queueLock);
        g_writerStarted = false;
        LeaveCriticalSection(&g_queueLock);
        return GetLastError();
    }

    CloseHandle(thread);
    return 0;
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookInitialize()
{
    // Start the writer first so nothing queued during startup sits undrained.
    WreckfestConsoleHookStartOutputWriter();

    DWORD reconnectResult = WreckfestConsoleHookReconnect();
    if (reconnectResult != 0)
    {
        return reconnectResult;
    }

    return WreckfestConsoleHookStartInput();
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        InitializeCriticalSection(&g_hookLock);
        InitializeCriticalSection(&g_outputLock);
        InitializeCriticalSection(&g_dispatchLock);
        InitializeCriticalSection(&g_queueLock);
        g_queueEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        g_locksReady = true;
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if ((g_hookInstalled && g_target != nullptr) || (g_chatHookInstalled && g_chatTarget != nullptr))
        {
            EnterCriticalSection(&g_hookLock);
            if (g_hookInstalled && g_target != nullptr)
            {
                RestoreHook();
                MH_RemoveHook(g_target);
                g_hookInstalled = false;
                g_originalConsolePrint = nullptr;
            }

            if (g_chatHookInstalled && g_chatTarget != nullptr)
            {
                RestoreChatHook();
                MH_RemoveHook(g_chatTarget);
                g_chatHookInstalled = false;
                g_chatOriginal = nullptr;
            }

            // One MinHook instance backs both hooks, so uninitialise once, after both.
            MH_Uninitialize();
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
