#include <windows.h>
#include <tlhelp32.h>

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
// Race results. Registry tags reached through the same lookup as SERVER, and the
// session globals that time a race. Confirmed live against an in-game results
// screen; see docs/finding-rvas.md.
constexpr uintptr_t GameplayRuleDataTagRva = 0x065E6278;
constexpr uintptr_t RacePositionsDataTagRva = 0x065E6224;
constexpr uintptr_t EventSettingsTagRva = 0x065E6890;
// Byte: set to 1 when the last human is done ("Event ended!"), cleared about 20 s
// later when the server returns to the lobby and the car records are reset.
constexpr uintptr_t EventEndedFlagRva = 0x019146E8;
// Int: incremented at every "Event started!". Not a racing flag (issue #189).
constexpr uintptr_t EventCounterRva = 0x019146EC;
// pRuleData->cars: a block of 0x110 byte car records at the head of the object.
// Car record i belongs to player-table slot i.
constexpr size_t RaceCarRecordSize = 0x110;
constexpr int MaxRaceCars = 32;
constexpr size_t PlayerRecordSize = 0x138;
constexpr int PlayerSlots = 24;
// FUN_14038fc10(int ringIndex, char* text, void* serverObject): the unified input
// handler for the server console and for player chat. See docs/finding-rvas.md.
constexpr uintptr_t ChatHandlerRva = 0x0038FC10;

// The per-slot input ring, documented in docs/finding-rvas.md and confirmed live:
// 24 entries of stride 0x1010, each a cumulative byte cursor followed by a 4096
// byte window holding messages verbatim, newline delimited, NUL padded past the
// cursor. The handler's own char* argument cannot be trusted - it arrives offset
// by (length & ~7) into the message, so only the final partial 8 byte block is
// readable through it - but the ring holds the whole thing.
constexpr uintptr_t InputRingBaseRva = 0x019149A0;
constexpr size_t InputRingStride = 0x1010;
constexpr size_t InputRingDataOffset = 0x10;
constexpr size_t InputRingWindow = 0x1000;
constexpr size_t InputRingEntries = 24;
// The game refuses anything longer, so a backward scan that passes this has lost
// its place and must give up rather than return a run of stale bytes.
constexpr size_t MaxInputRingMessage = 127;

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
bool g_hookInstalled = false;
bool g_inputStarted = false;
ConsolePrintFn g_originalConsolePrint = nullptr;
void* g_target = nullptr;
bool g_chatHookInstalled = false;
ChatHandlerFn g_chatOriginal = nullptr;
void* g_chatTarget = nullptr;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
wchar_t g_fallbackLogPath[MAX_PATH] = {};

// Teardown (issue #24). Every thread this module starts is registered here so
// WreckfestConsoleHookShutdown can stop and join it before anything shared is
// released. DllMain cannot do that itself: a thread cannot exit while the loader
// lock is held, so joining from DLL_PROCESS_DETACH would deadlock.
//
// The first worker also takes a reference on this module, and only a completed
// shutdown gives it back. A FreeLibrary that skips the shutdown therefore cannot
// unmap code that a worker or a hooked game thread is still running.
HMODULE g_module = nullptr;
HANDLE g_shutdownEvent = nullptr;
// Completion event for output pipe writes, which g_outputLock serialises.
HANDLE g_pipeIoEvent = nullptr;
CRITICAL_SECTION g_threadsLock;
std::vector<HANDLE> g_workerThreads;
HANDLE g_inputThread = nullptr;
bool g_shuttingDown = false;
bool g_selfReferenced = false;
CRITICAL_SECTION g_teardownLock;
bool g_teardownDone = false;
// Set while the input thread is inside FlushFileBuffers, the one pipe call it
// cannot make overlapped. See InputPipeThread.
volatile LONG g_inputFlushing = 0;
// Calls currently running module code on a thread this module does not own: game
// threads inside a detour, and callers of the start exports. Teardown waits for
// this to reach zero; see NoCallersInModule for the windows it cannot see.
volatile LONG g_activeCalls = 0;

struct ActiveCall
{
    ActiveCall() { InterlockedIncrement(&g_activeCalls); }
    ~ActiveCall() { InterlockedDecrement(&g_activeCalls); }
    ActiveCall(const ActiveCall&) = delete;
    ActiveCall& operator=(const ActiveCall&) = delete;
};

// Waits up to ms, returning early - and true - once teardown has begun.
bool WaitForShutdown(DWORD ms)
{
    return WaitForSingleObject(g_shutdownEvent, ms) == WAIT_OBJECT_0;
}

bool ShutdownRequested()
{
    return WaitForShutdown(0);
}

DWORD RemainingMs(ULONGLONG deadline)
{
    ULONGLONG now = GetTickCount64();
    return now < deadline ? static_cast<DWORD>(deadline - now) : 0;
}

// EnterCriticalSection with a deadline, so a lock held by a stalled thread turns
// into a timeout rather than a hang.
bool EnterBefore(CRITICAL_SECTION* lock, ULONGLONG deadline)
{
    while (!TryEnterCriticalSection(lock))
    {
        if (GetTickCount64() >= deadline)
        {
            return false;
        }

        Sleep(5);
    }

    return true;
}

// Starts a thread that teardown will join. Refused once teardown has begun, so no
// thread can appear after the list has been taken. started, when given, receives
// the thread's handle; it stays owned by the list.
DWORD StartWorker(LPTHREAD_START_ROUTINE routine, HANDLE* started = nullptr)
{
    EnterCriticalSection(&g_threadsLock);
    if (g_shuttingDown)
    {
        LeaveCriticalSection(&g_threadsLock);
        return ERROR_SHUTDOWN_IN_PROGRESS;
    }

    if (!g_selfReferenced)
    {
        HMODULE self = nullptr;
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(&StartWorker),
                &self))
        {
            DWORD error = GetLastError();
            LeaveCriticalSection(&g_threadsLock);
            return error;
        }

        g_selfReferenced = true;
    }

    // Every reconnect starts a HookThread, so drop the handles of finished ones.
    for (auto it = g_workerThreads.begin(); it != g_workerThreads.end();)
    {
        if (WaitForSingleObject(*it, 0) == WAIT_OBJECT_0)
        {
            if (*it == g_inputThread)
            {
                g_inputThread = nullptr;
            }

            CloseHandle(*it);
            it = g_workerThreads.erase(it);
        }
        else
        {
            ++it;
        }
    }

    DWORD result = ERROR_SUCCESS;
    HANDLE thread = CreateThread(nullptr, 0, routine, nullptr, 0, nullptr);
    if (thread == nullptr)
    {
        result = GetLastError();
    }
    else
    {
        g_workerThreads.push_back(thread);
        if (started != nullptr)
        {
            *started = thread;
        }
    }

    LeaveCriticalSection(&g_threadsLock);
    return result;
}

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
    // The formatted line, held back rather than written when it is recognised.
    // The record must reach the controller first, and the record cannot be built
    // until the handler returns - see HookedChatHandler.
    std::string consoleLine;
    bool haveConsoleLine = false;
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
        const uintptr_t dataRvas[] = {
            RegistryTablePtrRva, ServerNamespaceTagRva, GameplayRuleDataTagRva, RacePositionsDataTagRva,
            EventSettingsTagRva, EventEndedFlagRva, EventCounterRva };

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

// Appends "<label> <offset>: <hex>" lines, 32 bytes each. Faults propagate to the
// caller's __try.
void AppendHexDump(std::string& response, const char* label, uintptr_t address, size_t size)
{
    static const char Digits[] = "0123456789abcdef";
    char prefix[64] = {};
    for (size_t offset = 0; offset < size; offset += 32)
    {
        std::snprintf(prefix, sizeof(prefix), "%s +%03zX:", label, offset);
        response += prefix;
        for (size_t i = offset; i < offset + 32 && i < size; i++)
        {
            auto value = *reinterpret_cast<const unsigned char*>(address + i);
            if (i % 4 == 0)
            {
                response += ' ';
            }
            response += Digits[value >> 4];
            response += Digits[value & 0xF];
        }
        response += '\n';
    }
}

bool SafeCopy(uintptr_t address, unsigned char* buffer, size_t size)
{
    __try
    {
        std::memcpy(buffer, reinterpret_cast<const void*>(address), size);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// Like AppendHexDump, but each line also shows the bytes as text and an unreadable
// address is reported instead of faulting.
void AppendGuardedDump(std::string& response, const char* label, uintptr_t address, size_t size)
{
    static const char Digits[] = "0123456789abcdef";
    unsigned char bytes[0x100] = {};
    size = size > sizeof(bytes) ? sizeof(bytes) : size;
    char prefix[96] = {};
    if (!SafeCopy(address, bytes, size))
    {
        std::snprintf(prefix, sizeof(prefix), "%s unreadable 0x%llX\n", label, static_cast<unsigned long long>(address));
        response += prefix;
        return;
    }

    for (size_t offset = 0; offset < size; offset += 32)
    {
        std::snprintf(prefix, sizeof(prefix), "%s +%03zX:", label, offset);
        response += prefix;
        size_t end = offset + 32 < size ? offset + 32 : size;
        for (size_t i = offset; i < end; i++)
        {
            if (i % 4 == 0)
            {
                response += ' ';
            }
            response += Digits[bytes[i] >> 4];
            response += Digits[bytes[i] & 0xF];
        }
        response += "  |";
        for (size_t i = offset; i < end; i++)
        {
            response += bytes[i] >= 0x20 && bytes[i] < 0x7F ? static_cast<char>(bytes[i]) : '.';
        }
        response += "|\n";
    }
}

bool LooksLikePointer(uintptr_t value)
{
    return value >= 0x10000 && value < 0x00007FFFFFFFFFFFull && (value & 7) == 0;
}

// Dumps the object at address, then one level of the pointers it contains.
void AppendPointerTree(std::string& response, const char* label, uintptr_t address, size_t size)
{
    AppendGuardedDump(response, label, address, size);

    unsigned char bytes[0x100] = {};
    size = size > sizeof(bytes) ? sizeof(bytes) : size;
    if (!SafeCopy(address, bytes, size))
    {
        return;
    }

    char childLabel[64] = {};
    for (size_t offset = 0; offset + 8 <= size; offset += 8)
    {
        uintptr_t child = 0;
        std::memcpy(&child, bytes + offset, sizeof(child));
        if (!LooksLikePointer(child))
        {
            continue;
        }

        std::snprintf(childLabel, sizeof(childLabel), "%s.%02zX", label, offset);
        AppendGuardedDump(response, childLabel, child, 0x60);
    }
}

uintptr_t LookupRegistryObject(uintptr_t moduleBase, const char* name, uintptr_t tagRva)
{
    auto lookup = reinterpret_cast<RegistryLookupFn>(moduleBase + RegistryLookupRva);
    auto registryTable = *reinterpret_cast<uintptr_t*>(moduleBase + RegistryTablePtrRva);
    auto tag = *reinterpret_cast<uintptr_t*>(moduleBase + tagRva);
    if (registryTable == 0)
    {
        return 0;
    }

    int index = lookup(name, tag);
    if (index < 0)
    {
        return 0;
    }

    return *reinterpret_cast<uintptr_t*>(registryTable + static_cast<uintptr_t>(index) * 0x138 + 0x406040);
}

// Diagnostic for the race-results research: dumps pRuleData->cars from the
// gameplay_rule_data registry object, with the fields the decompiled
// SimulateFinishRace debug print reads, plus the raw record so unknown fields can
// be matched against the in-game results screen. Unconfirmed layout:
// +0x20 flags (1 finished, 2 wrecked, 8 ?, 0x10 DNF, 0x20 ?), +0x24 position
// (0-based), +0x25 laps, +0x40 best lap ms, +0x88 name, +0x90 race time ms.
bool ReadRaceResultsNoThrow(std::string& response)
{
    if (g_layoutStatus != LayoutStatus::Ok)
    {
        response = "ERR results module layout not validated\n";
        return false;
    }

    __try
    {
        auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        response.clear();
        response.reserve(32768);
        char line[512] = {};

        // Session globals around the parity bit (0x19146E0) and the event counter
        // (0x19146EC), so state can be correlated with each snapshot.
        AppendHexDump(response, "SESSION 19146D0", moduleBase + 0x19146D0, 0x20);

        auto positions = LookupRegistryObject(moduleBase, "race_positions_data", RacePositionsDataTagRva);
        std::snprintf(line, sizeof(line), "POSITIONS object=%s\n", positions == 0 ? "null" : "ok");
        response += line;
        if (positions != 0)
        {
            AppendHexDump(response, "POSITIONS", positions, 0x80);
        }

        auto ruleData = LookupRegistryObject(moduleBase, "gameplay_rule_data", GameplayRuleDataTagRva);
        if (ruleData == 0)
        {
            response += "ERR results gameplay_rule_data unavailable\n";
            return false;
        }

        AppendHexDump(response, "RULEDATA", ruleData, 0x200);

        auto cars = *reinterpret_cast<uintptr_t*>(ruleData);
        auto count = *reinterpret_cast<int*>(ruleData + 8);
        auto heading = *reinterpret_cast<uintptr_t*>(ruleData + 0x10);
        int elementSize = -1;
        if (heading != 0)
        {
            auto type = *reinterpret_cast<uintptr_t*>(heading);
            if (type != 0)
            {
                elementSize = *reinterpret_cast<int*>(type + 8);
            }
        }

        std::snprintf(line, sizeof(line), "CARS count=%d elementSize=0x%X expected=0x%zX\n",
            count, static_cast<unsigned int>(elementSize), RaceCarRecordSize);
        response += line;

        if (cars == 0 || count < 0 || count > MaxRaceCars)
        {
            response += "ERR results car block implausible\n";
            return false;
        }

        if (elementSize != -1 && elementSize != static_cast<int>(RaceCarRecordSize))
        {
            response += "ERR results car record size mismatch\n";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            auto car = cars + static_cast<uintptr_t>(i) * RaceCarRecordSize;
            auto flags = *reinterpret_cast<unsigned int*>(car + 0x20);
            auto position = *reinterpret_cast<unsigned char*>(car + 0x24);
            auto laps = *reinterpret_cast<unsigned char*>(car + 0x25);
            auto bestLap = *reinterpret_cast<int*>(car + 0x40);
            auto time = *reinterpret_cast<int*>(car + 0x90);
            auto namePtr = *reinterpret_cast<const char**>(car + 0x88);
            const char* name = namePtr == nullptr || namePtr[0] == '\0' ? "<none>" : namePtr;

            std::snprintf(line, sizeof(line),
                "CAR index=%d pos=%u laps=%u flags=0x%X time=%d bestLap=%d name=%.*s\n",
                i, static_cast<unsigned int>(position), static_cast<unsigned int>(laps),
                flags, time, bestLap, 64, name);
            response += line;

            char label[16] = {};
            std::snprintf(label, sizeof(label), "CAR%02d", i);
            AppendHexDump(response, label, car, RaceCarRecordSize);

            // +0x68 is a distinct heap pointer per car; the car model is not in the
            // record itself, so follow it looking for the vehicle.
            auto vehicle = *reinterpret_cast<uintptr_t*>(car + 0x68);
            if (LooksLikePointer(vehicle))
            {
                std::snprintf(label, sizeof(label), "VEH%02d", i);
                AppendPointerTree(response, label, vehicle, 0x80);
            }
        }

        std::snprintf(line, sizeof(line), "OK results count=%d\n", count);
        response += line;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        response += "ERR results raised an exception\n";
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

// Writes to the output pipe; the caller holds g_outputLock. The pipe is overlapped
// so that a controller which has stopped reading cannot hold up teardown: it gets
// a moment once shutdown begins and is then given up on. There is deliberately no
// FlushFileBuffers - it would wait on the controller with no way to cancel it, and
// a pipe keeps what was written for its reader even after this end closes.
bool WritePipeLocked(const char* data, DWORD size)
{
    constexpr DWORD ShutdownGraceMs = 1000;

    OVERLAPPED overlapped = {};
    overlapped.hEvent = g_pipeIoEvent;
    if (!WriteFile(g_pipe, data, size, nullptr, &overlapped) && GetLastError() != ERROR_IO_PENDING)
    {
        return false;
    }

    HANDLE waits[] = { overlapped.hEvent, g_shutdownEvent };
    if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0 &&
        WaitForSingleObject(overlapped.hEvent, ShutdownGraceMs) != WAIT_OBJECT_0)
    {
        CancelIoEx(g_pipe, &overlapped);
    }

    // Waits for the cancellation too, so overlapped is not released while in flight.
    DWORD written = 0;
    return GetOverlappedResult(g_pipe, &overlapped, &written, TRUE) && written == size;
}

// Performs the actual I/O. Called on the writer thread, and by teardown once the
// writer has stopped.
void WriteHookLineBlocking(const char* text)
{
    if (text == nullptr || *text == '\0')
    {
        return;
    }

    EnterCriticalSection(&g_outputLock);

    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        if (!WritePipeLocked(text, static_cast<DWORD>(std::strlen(text))) || !WritePipeLocked("\n", 1))
        {
            CloseHandle(g_pipe);
            g_pipe = INVALID_HANDLE_VALUE;
        }
    }

    WriteFallbackLog(text);
    LeaveCriticalSection(&g_outputLock);
}

void DrainOutputQueue()
{
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

DWORD WINAPI OutputWriterThread(void*)
{
    HANDLE waits[] = { g_queueEvent, g_shutdownEvent };
    for (;;)
    {
        bool stopping = WaitForMultipleObjects(2, waits, FALSE, 250) == WAIT_OBJECT_0 + 1;

        DrainOutputQueue();

        // Teardown drains once more after the hooks are out, so anything queued
        // between this exit and then still reaches the controller.
        if (stopping)
        {
            return 0;
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
bool CopyGameStringNoThrow(const char* text, size_t maxLength, std::string& out);
bool ConsoleLinePairsWithPendingChat(const std::string& line);
void EmitPendingChatRecord();


void __fastcall HookedConsolePrint(const char* text, void* arg2, void* arg3, void* arg4)
{
    ActiveCall inDetour;

    // The chat line is held back rather than written here. Two constraints meet at
    // this point: the controller warns about a chat-shaped line that arrived with
    // no record, so the record must be written first - and the record cannot be
    // built yet, because the game appends the message to the input ring only after
    // the handler returns. Both are satisfied by deferring this line to the end of
    // HookedChatHandler, which writes the record and then this.
    if (t_pendingChat.active && !t_pendingChat.emitted && !t_pendingChat.haveConsoleLine)
    {
        std::string candidate;
        if (CopyGameStringNoThrow(text, MaxConsoleLineLength, candidate) &&
            ConsoleLinePairsWithPendingChat(candidate))
        {
            t_pendingChat.consoleLine = candidate;
            t_pendingChat.haveConsoleLine = true;

            if (g_originalConsolePrint != nullptr)
            {
                g_originalConsolePrint(text, arg2, arg3, arg4);
            }

            return;
        }
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
bool EndsWith(const std::string& value, const std::string& suffix);
bool ReadRingCursorNoThrow(uintptr_t moduleBase, uintptr_t ringIndex, unsigned long long& cursor);
std::string TrimTrailingTerminator(const std::string& value);
std::string SanitizeRecordField(const std::string& value, size_t maxLength);

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

// The raw cursor, for diagnostics: reported when a ring read is rejected so the
// reason is visible instead of inferred.
bool ReadRingCursorNoThrow(uintptr_t moduleBase, uintptr_t ringIndex, unsigned long long& cursor)
{
    cursor = 0;

    __try
    {
        if (ringIndex >= InputRingEntries)
        {
            return false;
        }

        cursor = *reinterpret_cast<const unsigned long long*>(
            moduleBase + InputRingBaseRva + ringIndex * InputRingStride);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        cursor = 0;
        return false;
    }
}

// Recovers the message the player actually sent, from the input ring rather than
// from the handler's char* argument. The cursor counts cumulative bytes including
// each trailing newline, so the newest message is the run ending at cursor-1.
//
// Guarded like every other read of game memory: a bad index or a torn cursor must
// cost one message, never the server process.
bool ReadRingMessageNoThrow(uintptr_t moduleBase, uintptr_t ringIndex, std::string& out)
{
    out.clear();

    __try
    {
        if (ringIndex >= InputRingEntries)
        {
            return false;
        }

        auto entry = moduleBase + InputRingBaseRva + ringIndex * InputRingStride;
        auto cursor = *reinterpret_cast<const unsigned long long*>(entry);
        auto data = reinterpret_cast<const char*>(entry + InputRingDataOffset);

        // cursor-1 is the newline this message ended with; everything before it,
        // back to the previous newline, is the message.
        if (cursor == 0)
        {
            return false;
        }

        unsigned long long end = cursor - 1;
        if (data[end % InputRingWindow] != 10)
        {
            // Not sitting on a terminator: the message is not in the ring yet, or
            // the cursor moved under us. Either way this is not ours to guess at.
            return false;
        }

        unsigned long long start = end;
        while (start > 0 &&
               (end - start) < MaxInputRingMessage &&
               data[(start - 1) % InputRingWindow] != 10)
        {
            start--;
        }

        for (unsigned long long i = start; i < end; i++)
        {
            out.push_back(data[i % InputRingWindow]);
        }

        return !out.empty();
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
    ActiveCall inDetour;

    // Saved and restored rather than simply cleared, so a nested call (the console
    // path re-entering the handler) cannot lose the outer call's state.
    PendingChat saved = t_pendingChat;

    t_pendingChat.active = true;
    t_pendingChat.emitted = false;
    t_pendingChat.ringIndex = static_cast<int>(ringIndex);

    // Only the fragment here. `text` is offset by (length & ~7) into the message,
    // so it yields the final partial 8 byte block and nothing else, but the ring
    // cannot be read yet: at entry the game has not appended this message, so the
    // cursor still ends on the previous one. The whole message is recovered at
    // emit time instead. See TryEmitStructuredChat.
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

    // Only now does the message exist in the input ring, so this is the first
    // moment a complete record can be built. The console line was held back in
    // HookedConsolePrint so the record still reaches the controller first.
    if (t_pendingChat.haveConsoleLine)
    {
        EmitPendingChatRecord();
        WriteHookLine(t_pendingChat.consoleLine.c_str());
    }

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
// The input ring is newline delimited, so a captured message carries its
// terminator while the formatted console line does not. It must come off before
// sanitising: SanitizeRecordField rewrites every control byte to '?', which turns
// the terminator into an ordinary character that no downstream trim can remove -
// "!help" ships as "!help?" and never matches the command it names.
bool EndsWith(const std::string& value, const std::string& suffix)
{
    return suffix.size() <= value.size() &&
           value.compare(value.size() - suffix.size(), suffix.size(), suffix) == 0;
}

std::string TrimTrailingTerminator(const std::string& value)
{
    size_t end = value.size();
    while (end > 0 && (value[end - 1] == 10 || value[end - 1] == 13 || value[end - 1] == 32))
    {
        end--;
    }

    return value.substr(0, end);
}

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
// Is this the formatted line for the message currently being handled? The check is
// containment of the captured fragment, which is a suffix of the real message, so a
// line carrying the message always matches and the handler's other output does not.
bool ConsoleLinePairsWithPendingChat(const std::string& line)
{
    std::string probe = TrimTrailingTerminator(t_pendingChat.rawText);
    return !probe.empty() && line.find(probe) != std::string::npos;
}

// Builds and writes the record for the message just handled. Called after the
// original handler returns, which is the first moment the message exists in the
// input ring - the handler's own char* argument is offset by (length & ~7) into the
// message and exposes only its final partial 8 byte block.
void EmitPendingChatRecord()
{
    const std::string& line = t_pendingChat.consoleLine;

    // The fragment is kept as a check rather than a payload. It is a suffix of the
    // real message by construction, so the ring text must end with it, and the ring
    // text must also appear in the line the game formatted. Two independent sources
    // have to agree before the message is believed; a wrong message is worse than a
    // truncated one.
    std::string fragment = TrimTrailingTerminator(t_pendingChat.rawText);
    auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));

    std::string fromRing;
    bool ringOk = ReadRingMessageNoThrow(moduleBase, static_cast<uintptr_t>(t_pendingChat.ringIndex), fromRing);
    bool endsOk = ringOk && EndsWith(fromRing, fragment);
    bool inLineOk = ringOk && line.find(fromRing) != std::string::npos;

    std::string message;
    if (ringOk && endsOk && inLineOk)
    {
        message = fromRing;
    }
    else
    {
        // Fall back to the fragment, which is what shipped before the ring was
        // used at all, and report the state that caused the rejection. Diagnosing
        // this from outside the process is guesswork: a separate read cannot see
        // what was true during the call.
        unsigned long long cursor = 0;
        ReadRingCursorNoThrow(moduleBase, static_cast<uintptr_t>(t_pendingChat.ringIndex), cursor);

        std::string warn = "WreckfestConsoleHook ring rejected: index=";
        warn += std::to_string(t_pendingChat.ringIndex);
        warn += " cursor=";
        warn += std::to_string(cursor);
        warn += " read=";
        warn += ringOk ? "ok" : "FAILED";
        warn += " endsWithFragment=";
        warn += endsOk ? "yes" : "NO";
        warn += " inConsoleLine=";
        warn += inLineOk ? "yes" : "NO";
        warn += " ring=[";
        warn += SanitizeRecordField(fromRing, MaxChatMessageLength);
        warn += "] fragment=[";
        warn += SanitizeRecordField(fragment, MaxChatMessageLength);
        warn += "]";
        WriteHookLine(warn.c_str());

        message = fragment;
    }

    if (message.empty())
    {
        WriteHookLine("WreckfestConsoleHook chat capture was empty; no record emitted");
        return;
    }

    // Deliberately no interpretation here. Everything this says is something the
    // hook observed: which ring entry, the message, and the line the game formatted
    // from it. Working out the sender means reasoning about "^8", "^0" and the ": "
    // separator, and that belongs in HookChatRecord where it is unit tested and
    // where a mistake costs a dropped command rather than a dead game process.
    std::string record;
    record.reserve(message.size() + line.size() + 32);
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
    record += SanitizeRecordField(message, MaxChatMessageLength);
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
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
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
        if (WaitForShutdown(100))
        {
            return;
        }
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

// The SERVER object's session state machine (issue #189): an int at +0x4 and its
// timer in ms at +0x10. 0 lobby, 1 countdown, 2 racing, 3 results, 4 the handover
// to the next event in the loop. See docs/finding-rvas.md.
constexpr size_t ServerSessionStateOffset = 0x4;
constexpr size_t ServerSessionTimerOffset = 0x10;

struct SessionSnapshot
{
    int state;
    int timerMs;
    int eventCounter;
    unsigned char ended;
};

// POD only, so the SEH guard needs no unwinding: a stale offset after a game patch
// must cost the answer, not the game.
bool ReadSessionNoThrow(SessionSnapshot& snapshot)
{
    __try
    {
        auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        auto server = LookupRegistryObject(moduleBase, "SERVER", ServerNamespaceTagRva);
        if (server == 0)
        {
            return false;
        }

        snapshot.state = *reinterpret_cast<int*>(server + ServerSessionStateOffset);
        snapshot.timerMs = *reinterpret_cast<int*>(server + ServerSessionTimerOffset);
        snapshot.eventCounter = *reinterpret_cast<int*>(moduleBase + EventCounterRva);
        snapshot.ended = *reinterpret_cast<unsigned char*>(moduleBase + EventEndedFlagRva);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

std::string HandleSessionCommand()
{
    if (g_layoutStatus != LayoutStatus::Ok)
    {
        return "ERR session module layout not validated" NLSTR;
    }

    SessionSnapshot snapshot = {};
    if (!ReadSessionNoThrow(snapshot))
    {
        return "ERR session SERVER object unreadable" NLSTR;
    }

    char line[160] = {};
    std::snprintf(line, sizeof(line), "OK session state=%d timer=%d counter=%d ended=%u" NLSTR,
        snapshot.state, snapshot.timerMs, snapshot.eventCounter, static_cast<unsigned>(snapshot.ended));
    return line;
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

    if (commandLine == "__hook_session")
    {
        return HandleSessionCommand();
    }

    if (commandLine == "__hook_results")
    {
        std::string response;
        ReadRaceResultsNoThrow(response);
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

// Completes one overlapped operation on the input pipe, or cancels it once teardown
// begins. started is what the call that began the operation returned. True only
// when it completed successfully.
bool FinishPipeIo(HANDLE pipe, OVERLAPPED& overlapped, BOOL started, DWORD& transferred)
{
    transferred = 0;
    if (!started && GetLastError() != ERROR_IO_PENDING)
    {
        return false;
    }

    // The I/O event is listed first, so an operation that has completed wins over a
    // shutdown signalled at the same moment.
    HANDLE waits[] = { overlapped.hEvent, g_shutdownEvent };
    if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0)
    {
        CancelIoEx(pipe, &overlapped);
        // The kernel writes to overlapped until the cancellation lands; wait for it
        // so the structure is not reused while still in flight.
        GetOverlappedResult(pipe, &overlapped, &transferred, TRUE);
        transferred = 0;
        return false;
    }

    return GetOverlappedResult(pipe, &overlapped, &transferred, FALSE) != FALSE;
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

    // The pipe is overlapped so that teardown can wake a thread waiting for a
    // controller. CancelSynchronousIo would also do that, but it cancels whatever
    // the thread is blocked in - including the game's own I/O mid-dispatch.
    HANDLE ioEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (ioEvent == nullptr)
    {
        WriteHookLine("WreckfestConsoleHook input pipe could not create its I/O event.");
        return 0;
    }

    while (!ShutdownRequested())
    {
        HANDLE pipe = CreateNamedPipeW(
            pipeName,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            4096,
            4096,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            WriteFallbackLog("WreckfestConsoleHook input pipe CreateNamedPipeW failed.");
            WaitForShutdown(1000);
            continue;
        }

        OVERLAPPED overlapped = {};
        overlapped.hEvent = ioEvent;
        DWORD transferred = 0;

        BOOL connectReturned = ConnectNamedPipe(pipe, &overlapped);
        bool connected = (!connectReturned && GetLastError() == ERROR_PIPE_CONNECTED) ||
            FinishPipeIo(pipe, overlapped, connectReturned, transferred);
        if (connected)
        {
            char buffer[2048] = {};
            std::string response;

            overlapped = {};
            overlapped.hEvent = ioEvent;
            BOOL readReturned = ReadFile(pipe, buffer, sizeof(buffer) - 1, nullptr, &overlapped);
            if (FinishPipeIo(pipe, overlapped, readReturned, transferred) && transferred > 0)
            {
                buffer[transferred] = '\0';
                response = HandleInputCommand(buffer);
            }
            else
            {
                response = "ERR read failed\n";
            }

            overlapped = {};
            overlapped.hEvent = ioEvent;
            BOOL writeReturned = WriteFile(pipe, response.c_str(), static_cast<DWORD>(response.size()), nullptr, &overlapped);
            FinishPipeIo(pipe, overlapped, writeReturned, transferred);

            // Waits for the controller to read the response, so that disconnecting
            // below cannot discard it. There is no overlapped form, so teardown
            // cancels it through this flag - see JoinWorker.
            InterlockedExchange(&g_inputFlushing, 1);
            FlushFileBuffers(pipe);
            InterlockedExchange(&g_inputFlushing, 0);
        }

        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }

    CloseHandle(ioEvent);
    return 0;
}

// ---- Race results ----------------------------------------------------------
//
// When the race-ended flag rises, every car's result is still in memory for about
// 20 s. The watcher snapshots it once and ships it as a RACE record:
//
//   \x12RACE \x1f <header fields> { \x1f <car fields> } \x13
//
// Header, in order: version, eventCounter, trackId, laps, gameMode, startedUnixMs
// (0 when the start was not seen), endedUnixMs, carCount.
// Each car, in order: slot, playerStatus, playerFlags, steamId, name, position,
// lap, carFlags, timeMs, bestLapMs, finishMs, classIndex, rating, cupPoints,
// vehicleKey, vehicleName.
//
// Like CHAT, every field is something observed in memory. What counts as a bot, a
// DNF or a projected finish is decided in HookRaceRecord, where it is unit tested.

constexpr int RaceRecordVersion = 1;
constexpr size_t MaxRaceStringLength = 96;

bool g_raceWatcherStarted = false;

uintptr_t LookupRegistryObjectNoThrow(uintptr_t moduleBase, const char* name, uintptr_t tagRva)
{
    __try
    {
        return LookupRegistryObject(moduleBase, name, tagRva);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}

template <typename T>
T ReadAt(const unsigned char* buffer, size_t offset)
{
    T value{};
    std::memcpy(&value, buffer + offset, sizeof(T));
    return value;
}

unsigned long long UnixTimeMs()
{
    FILETIME fileTime = {};
    GetSystemTimeAsFileTime(&fileTime);
    ULARGE_INTEGER ticks = {};
    ticks.LowPart = fileTime.dwLowDateTime;
    ticks.HighPart = fileTime.dwHighDateTime;
    // FILETIME counts 100 ns intervals from 1601-01-01.
    return (ticks.QuadPart - 116444736000000000ull) / 10000ull;
}

void AppendField(std::string& record, const std::string& value)
{
    record.push_back(FieldSeparator);
    record += value;
}

void AppendField(std::string& record, unsigned long long value)
{
    char text[32] = {};
    std::snprintf(text, sizeof(text), "%llu", value);
    AppendField(record, std::string(text));
}

void AppendSignedField(std::string& record, long long value)
{
    char text[32] = {};
    std::snprintf(text, sizeof(text), "%lld", value);
    AppendField(record, std::string(text));
}

std::string ReadGameString(uintptr_t pointer)
{
    std::string value;
    if (!LooksLikePointer(pointer) ||
        !CopyGameStringNoThrow(reinterpret_cast<const char*>(pointer), MaxRaceStringLength, value))
    {
        return std::string();
    }

    return SanitizeRecordField(value, MaxRaceStringLength);
}

// Returns false with a reason when the race state cannot be read. Every read is
// guarded: a stale offset after a game patch must cost the record, not the game.
bool BuildRaceRecord(std::string& record, int eventCounter, unsigned long long startedMs,
    unsigned long long endedMs, std::string& error)
{
    auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));

    auto eventSettings = LookupRegistryObjectNoThrow(moduleBase, "event_settings", EventSettingsTagRva);
    auto ruleData = LookupRegistryObjectNoThrow(moduleBase, "gameplay_rule_data", GameplayRuleDataTagRva);
    auto server = LookupRegistryObjectNoThrow(moduleBase, "SERVER", ServerNamespaceTagRva);
    if (eventSettings == 0 || ruleData == 0 || server == 0)
    {
        error = "registry lookup failed";
        return false;
    }

    unsigned char settings[0x110] = {};
    if (!SafeCopy(eventSettings, settings, sizeof(settings)))
    {
        error = "event_settings unreadable";
        return false;
    }

    // pRuleData->cars is a block: pointer, count, then a heading whose type records
    // the element size. Checking that size catches a moved structure.
    unsigned char block[0x18] = {};
    uintptr_t type = 0;
    int elementSize = 0;
    if (!SafeCopy(ruleData, block, sizeof(block)) ||
        !SafeCopy(ReadAt<uintptr_t>(block, 0x10), reinterpret_cast<unsigned char*>(&type), sizeof(type)) ||
        !SafeCopy(type + 8, reinterpret_cast<unsigned char*>(&elementSize), sizeof(elementSize)))
    {
        error = "car block unreadable";
        return false;
    }

    auto cars = ReadAt<uintptr_t>(block, 0);
    auto count = ReadAt<int>(block, 8);
    if (elementSize != static_cast<int>(RaceCarRecordSize) || !LooksLikePointer(cars) ||
        count < 0 || count > MaxRaceCars)
    {
        error = "car block implausible";
        return false;
    }

    uintptr_t players = 0;
    if (!SafeCopy(server + 0x30, reinterpret_cast<unsigned char*>(&players), sizeof(players)) ||
        !LooksLikePointer(players))
    {
        error = "player table unreadable";
        return false;
    }

    std::string body;
    int included = 0;
    int slots = count < PlayerSlots ? count : PlayerSlots;
    for (int slot = 0; slot < slots; slot++)
    {
        unsigned char car[RaceCarRecordSize] = {};
        unsigned char player[PlayerRecordSize] = {};
        if (!SafeCopy(cars + static_cast<uintptr_t>(slot) * RaceCarRecordSize, car, sizeof(car)) ||
            !SafeCopy(players + static_cast<uintptr_t>(slot) * PlayerRecordSize, player, sizeof(player)))
        {
            error = "car or player record unreadable";
            return false;
        }

        // Unused slots carry no name; they were never in this race.
        auto name = ReadGameString(ReadAt<uintptr_t>(car, 0x88));
        if (name.empty())
        {
            continue;
        }

        std::string vehicleKey;
        std::string vehicleName;
        unsigned char vehicle[0x20] = {};
        auto vehiclePointer = ReadAt<uintptr_t>(car, 0x68);
        if (LooksLikePointer(vehiclePointer) && SafeCopy(vehiclePointer, vehicle, sizeof(vehicle)))
        {
            vehicleKey = ReadGameString(ReadAt<uintptr_t>(vehicle, 0x08));
            vehicleName = ReadGameString(ReadAt<uintptr_t>(vehicle, 0x18));
        }

        AppendSignedField(body, slot);
        AppendField(body, ReadAt<unsigned char>(player, 0xA6));
        AppendField(body, ReadAt<unsigned short>(player, 0x82));
        AppendField(body, ReadAt<unsigned long long>(player, 0x100));
        AppendField(body, name);
        AppendField(body, ReadAt<unsigned char>(car, 0x24));
        AppendField(body, ReadAt<unsigned char>(car, 0x25));
        AppendField(body, ReadAt<unsigned int>(car, 0x20));
        AppendSignedField(body, ReadAt<int>(car, 0x90));
        AppendSignedField(body, ReadAt<int>(car, 0x40));
        AppendSignedField(body, ReadAt<int>(car, 0x44));
        AppendSignedField(body, ReadAt<int>(car, 0x50));
        AppendSignedField(body, ReadAt<int>(car, 0x54));
        AppendField(body, ReadAt<unsigned short>(car, 0xEC));
        AppendField(body, vehicleKey);
        AppendField(body, vehicleName);
        included++;
    }

    record.clear();
    record.push_back(RecordStart);
    record += "RACE";
    AppendSignedField(record, RaceRecordVersion);
    AppendSignedField(record, eventCounter);
    AppendField(record, ReadGameString(ReadAt<uintptr_t>(settings, 0xB0)));
    AppendSignedField(record, ReadAt<int>(settings, 0x108));
    AppendSignedField(record, ReadAt<int>(settings, 0x38));
    AppendField(record, startedMs);
    AppendField(record, endedMs);
    AppendSignedField(record, included);
    record += body;
    record.push_back(RecordEnd);
    return true;
}

bool ReadRaceStateNoThrow(uintptr_t moduleBase, unsigned char& ended, int& counter)
{
    return SafeCopy(moduleBase + EventEndedFlagRva, &ended, sizeof(ended)) &&
        SafeCopy(moduleBase + EventCounterRva, reinterpret_cast<unsigned char*>(&counter), sizeof(counter));
}

DWORD WINAPI RaceWatcherThread(void*)
{
    constexpr DWORD PollMs = 250;
    // The ended flag rises in the same handler that finalises the results; waiting
    // a moment keeps the snapshot clear of anything still being written that frame.
    constexpr DWORD SettleMs = 1000;

    auto moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    unsigned char lastEnded = 0;
    int lastCounter = 0;
    unsigned long long startedMs = 0;

    // Prime the counter only. The ended flag starts as "not ended", so a hook that
    // arrives while a results screen is up still reports that race, with its start
    // unknown. Nothing can report a race twice: a reconnect into the same process
    // never starts a second watcher, and this one never primes again.
    unsigned char primedEnded = 0;
    while (!ReadRaceStateNoThrow(moduleBase, primedEnded, lastCounter))
    {
        if (WaitForShutdown(PollMs))
        {
            return 0;
        }
    }

    for (;;)
    {
        if (WaitForShutdown(PollMs))
        {
            return 0;
        }

        unsigned char ended = 0;
        int counter = 0;
        if (!ReadRaceStateNoThrow(moduleBase, ended, counter))
        {
            continue;
        }

        if (counter != lastCounter)
        {
            startedMs = counter > lastCounter ? UnixTimeMs() : 0;
            lastCounter = counter;
        }

        if (ended != 0 && lastEnded == 0)
        {
            auto endedMs = UnixTimeMs();
            if (WaitForShutdown(SettleMs))
            {
                return 0;
            }

            std::string record;
            std::string error;
            if (!ReadRaceStateNoThrow(moduleBase, ended, counter) || ended == 0)
            {
                WriteHookLine("WreckfestConsoleHook race results skipped: the results were gone before the snapshot.");
            }
            else if (BuildRaceRecord(record, counter, startedMs, endedMs, error))
            {
                WriteHookLine(record.c_str());
            }
            else
            {
                std::string line = "WreckfestConsoleHook race results unreadable: " + error;
                WriteHookLine(line.c_str());
            }

            startedMs = 0;
        }

        lastEnded = ended;
    }
}

void StartRaceWatcher()
{
    if (g_raceWatcherStarted)
    {
        return;
    }

    if (StartWorker(RaceWatcherThread) != ERROR_SUCCESS)
    {
        WriteHookLine("WreckfestConsoleHook failed to start the race results watcher.");
        return;
    }

    g_raceWatcherStarted = true;
    WriteHookLine("WreckfestConsoleHook race results watcher started.");
}

DWORD WINAPI HookThread(void*)
{
    InitializeFallbackLogPath();
    ConnectPipe();

    // Teardown joins this thread before removing the hooks, so installing them
    // after this check is still safe; it only saves the work.
    if (ShutdownRequested())
    {
        return 0;
    }

    EnterCriticalSection(&g_hookLock);
    if (g_hookInstalled)
    {
        WriteHookLine("WreckfestConsoleHook hook already installed; output reconnected.");
        // Retried here because a failed start would otherwise last until the game
        // restarts; it does nothing once the watcher is running.
        StartRaceWatcher();
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

    // Reads memory only and patches nothing, so it runs whatever happened above.
    StartRaceWatcher();
    LeaveCriticalSection(&g_hookLock);

    return 0;
}
bool AddressInModule(DWORD64 address)
{
    auto base = reinterpret_cast<uintptr_t>(g_module);
    auto nt = GetNtHeaders(base);
    return nt != nullptr && address >= base && address < base + nt->OptionalHeader.SizeOfImage;
}

// True when no other thread is running code in this module. Run only once both
// hooks are disabled, so no thread can newly enter a detour.
//
// The counter covers a thread anywhere inside a detour or a start export,
// including when it has called out of the module - into the game through a
// trampoline, or into the CRT. It cannot cover the few instructions before the
// counter goes up or after it comes down, so every other thread is also suspended
// and its instruction pointer checked against this module's image. Together they
// leave no window: a thread is either counted or visibly executing here.
//
// Nothing allocates while threads are suspended - one of them may hold the heap
// lock - so the list is built and its storage reserved first.
bool NoCallersInModule()
{
    if (InterlockedCompareExchange(&g_activeCalls, 0, 0) != 0)
    {
        return false;
    }

    std::vector<DWORD> threadIds;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    THREADENTRY32 entry = {};
    entry.dwSize = sizeof(entry);
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry))
    {
        if (entry.th32OwnerProcessID == GetCurrentProcessId() && entry.th32ThreadID != GetCurrentThreadId())
        {
            threadIds.push_back(entry.th32ThreadID);
        }
    }
    CloseHandle(snapshot);

    std::vector<HANDLE> suspended;
    suspended.reserve(threadIds.size());

    bool idle = true;
    for (DWORD id : threadIds)
    {
        HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, id);
        if (thread == nullptr)
        {
            // Exited since the snapshot.
            continue;
        }

        if (SuspendThread(thread) == static_cast<DWORD>(-1))
        {
            CloseHandle(thread);
            continue;
        }

        suspended.push_back(thread);

        // GetThreadContext also waits for the suspension to take effect.
        CONTEXT context = {};
        context.ContextFlags = CONTEXT_CONTROL;
        if (!GetThreadContext(thread, &context) || AddressInModule(context.Rip))
        {
            idle = false;
            break;
        }
    }

    // A thread may have entered a counted region before it was suspended.
    if (idle && InterlockedCompareExchange(&g_activeCalls, 0, 0) != 0)
    {
        idle = false;
    }

    for (HANDLE thread : suspended)
    {
        ResumeThread(thread);
        CloseHandle(thread);
    }

    return idle;
}

// Takes the hooks out of the game, by deadline. Disabling puts both entry points
// back; the trampolines are freed only once no thread can still be running through
// one. Every MinHook result is checked: false means a hook may still be in place,
// so the module must not be unloaded. A retry picks up where this stopped.
//
// Runs only after every worker has been joined, so nothing else touches the hook
// state and g_hookLock is not taken. The chat detour holds that lock while it runs
// the game's handler, so taking it here would wait on the game.
bool RemoveHooks(ULONGLONG deadline)
{
    bool console = g_hookInstalled && g_target != nullptr;
    bool chat = g_chatHookInstalled && g_chatTarget != nullptr;

    // MH_ERROR_DISABLED: an earlier attempt already got this far.
    if (console)
    {
        MH_STATUS status = MH_DisableHook(g_target);
        if (status != MH_OK && status != MH_ERROR_DISABLED)
        {
            return false;
        }
    }

    if (chat)
    {
        MH_STATUS status = MH_DisableHook(g_chatTarget);
        if (status != MH_OK && status != MH_ERROR_DISABLED)
        {
            return false;
        }
    }

    while (!NoCallersInModule())
    {
        if (GetTickCount64() >= deadline)
        {
            return false;
        }

        Sleep(10);
    }

    if (console)
    {
        if (MH_RemoveHook(g_target) != MH_OK)
        {
            return false;
        }

        g_hookInstalled = false;
        g_originalConsolePrint = nullptr;
    }

    if (chat)
    {
        if (MH_RemoveHook(g_chatTarget) != MH_OK)
        {
            return false;
        }

        g_chatHookInstalled = false;
        g_chatOriginal = nullptr;
    }

    // MinHook may have been initialised with neither hook installed, or not at all.
    MH_STATUS status = MH_Uninitialize();
    return status == MH_OK || status == MH_ERROR_NOT_INITIALIZED;
}

// Joins one worker by deadline. The input thread may be in FlushFileBuffers,
// which nothing else can wake, so it is cancelled there. Only there: anywhere
// else CancelSynchronousIo could cancel the game's own I/O mid-dispatch. The
// shutdown event is already set, so once out of the flush the thread cannot start
// another dispatch - the cancel can only ever land on the flush or on the pipe
// cleanup after it.
bool JoinWorker(HANDLE thread, HANDLE inputThread, ULONGLONG deadline)
{
    for (;;)
    {
        DWORD slice = RemainingMs(deadline);
        if (slice > 50)
        {
            slice = 50;
        }

        if (WaitForSingleObject(thread, slice) == WAIT_OBJECT_0)
        {
            return true;
        }

        if (GetTickCount64() >= deadline)
        {
            return false;
        }

        if (thread == inputThread && InterlockedCompareExchange(&g_inputFlushing, 0, 0) != 0)
        {
            CancelSynchronousIo(thread);
        }
    }
}
}

// 3: adds WreckfestConsoleHookShutdown.
extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookVersion()
{
    return 3;
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
    ActiveCall inExport;
    return StartWorker(HookThread);
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookStartInput()
{
    ActiveCall inExport;

    EnterCriticalSection(&g_dispatchLock);
    if (g_inputStarted)
    {
        LeaveCriticalSection(&g_dispatchLock);
        return 0;
    }

    g_inputStarted = true;
    LeaveCriticalSection(&g_dispatchLock);

    DWORD result = StartWorker(InputPipeThread, &g_inputThread);
    if (result != ERROR_SUCCESS)
    {
        EnterCriticalSection(&g_dispatchLock);
        g_inputStarted = false;
        LeaveCriticalSection(&g_dispatchLock);
    }

    return result;
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookStartOutputWriter()
{
    ActiveCall inExport;

    EnterCriticalSection(&g_queueLock);
    bool alreadyStarted = g_writerStarted;
    g_writerStarted = true;
    LeaveCriticalSection(&g_queueLock);

    if (alreadyStarted)
    {
        return 0;
    }

    DWORD result = StartWorker(OutputWriterThread);
    if (result != ERROR_SUCCESS)
    {
        EnterCriticalSection(&g_queueLock);
        g_writerStarted = false;
        LeaveCriticalSection(&g_queueLock);
    }

    return result;
}

extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookInitialize()
{
    ActiveCall inExport;

    // Start the writer first so nothing queued during startup sits undrained.
    WreckfestConsoleHookStartOutputWriter();

    DWORD reconnectResult = WreckfestConsoleHookReconnect();
    if (reconnectResult != 0)
    {
        return reconnectResult;
    }

    return WreckfestConsoleHookStartInput();
}

// Stops every thread this module started, takes the hooks out of the game, closes
// the output pipe and gives back the module reference the first worker took.
//
// It must be a thread's start routine - call it through CreateRemoteThread like
// the other exports - because on success it ends that thread with
// FreeLibraryAndExitThread, exit code 0: if a FreeLibrary has already dropped every
// other reference, the module is unmapped as this thread leaves it. Unload only
// after it returned 0, and with no other export call in flight.
//
// WAIT_TIMEOUT means a worker, a game thread inside a detour, or a lock holder was
// still busy at the deadline. Nothing has been released and the module stays
// pinned, so a premature FreeLibrary cannot unmap it; calling again retries. The
// hook does not restart afterwards: the start exports refuse with
// ERROR_SHUTDOWN_IN_PROGRESS.
extern "C" __declspec(dllexport) DWORD WreckfestConsoleHookShutdown()
{
    constexpr DWORD TimeoutMs = 5000;
    ULONGLONG deadline = GetTickCount64() + TimeoutMs;

    if (!EnterBefore(&g_teardownLock, deadline))
    {
        return WAIT_TIMEOUT;
    }

    if (g_teardownDone)
    {
        LeaveCriticalSection(&g_teardownLock);
        return 0;
    }

    if (!EnterBefore(&g_threadsLock, deadline))
    {
        LeaveCriticalSection(&g_teardownLock);
        return WAIT_TIMEOUT;
    }

    g_shuttingDown = true;
    std::vector<HANDLE> threads = g_workerThreads;
    HANDLE inputThread = g_inputThread;
    LeaveCriticalSection(&g_threadsLock);

    SetEvent(g_shutdownEvent);

    // Before the hooks: a HookThread still running could otherwise install them
    // again after they were removed.
    for (HANDLE thread : threads)
    {
        if (!JoinWorker(thread, inputThread, deadline))
        {
            // Not through the pipe: the worker still running may hold g_outputLock.
            WriteFallbackLog("WreckfestConsoleHook shutdown timed out waiting for a worker thread.");
            LeaveCriticalSection(&g_teardownLock);
            return WAIT_TIMEOUT;
        }
    }

    EnterCriticalSection(&g_threadsLock);
    for (HANDLE thread : g_workerThreads)
    {
        CloseHandle(thread);
    }
    g_workerThreads.clear();
    g_inputThread = nullptr;
    LeaveCriticalSection(&g_threadsLock);

    if (!RemoveHooks(deadline))
    {
        WriteFallbackLog("WreckfestConsoleHook shutdown could not remove the hooks before its deadline.");
        LeaveCriticalSection(&g_teardownLock);
        return WAIT_TIMEOUT;
    }

    // The writer is gone and the hooks are out, so this is the last of the output.
    // Uncontended now: g_outputLock was only ever taken by the writer and the hooks.
    DrainOutputQueue();
    WriteHookLineBlocking("WreckfestConsoleHook shut down.");
    ClosePipe();

    g_teardownDone = true;
    bool release = g_selfReferenced;
    g_selfReferenced = false;
    LeaveCriticalSection(&g_teardownLock);

    if (release)
    {
        FreeLibraryAndExitThread(g_module, 0);
    }

    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        InitializeCriticalSection(&g_hookLock);
        InitializeCriticalSection(&g_outputLock);
        InitializeCriticalSection(&g_dispatchLock);
        InitializeCriticalSection(&g_queueLock);
        InitializeCriticalSection(&g_threadsLock);
        InitializeCriticalSection(&g_teardownLock);
        g_queueEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        // Manual reset: once set, every waiter sees it.
        g_shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        g_pipeIoEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

        // The workers wait on these; with one missing they would spin, never stop,
        // or never write. Refusing the load is the safe outcome.
        if (g_queueEvent == nullptr || g_shutdownEvent == nullptr || g_pipeIoEvent == nullptr)
        {
            return FALSE;
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // The process is exiting. Every other thread has already been terminated and
        // the address space goes with the process, so touch nothing: a lock could
        // have been orphaned mid-hold by one of those threads.
        if (reserved != nullptr)
        {
            return TRUE;
        }

        // Unloading. The first worker pinned this module and only a completed
        // shutdown unpins it, so reaching here means either that shutdown joined
        // every worker and removed the hooks, or that nothing was ever started.
        // Either way no other thread can be using any of this.
        DeleteCriticalSection(&g_outputLock);
        DeleteCriticalSection(&g_hookLock);
        DeleteCriticalSection(&g_dispatchLock);
        DeleteCriticalSection(&g_queueLock);
        DeleteCriticalSection(&g_threadsLock);
        DeleteCriticalSection(&g_teardownLock);

        for (HANDLE event : { g_queueEvent, g_shutdownEvent, g_pipeIoEvent })
        {
            if (event != nullptr)
            {
                CloseHandle(event);
            }
        }
    }

    return TRUE;
}
