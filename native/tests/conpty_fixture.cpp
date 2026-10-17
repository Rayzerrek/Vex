#include "windows_conpty.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

namespace {
DWORD WINAPI copyOutput(void* context) {
    const HANDLE pipe = static_cast<HANDLE>(context);
    const HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    char bytes[4096];
    DWORD length, error = ERROR_SUCCESS;
    while (ReadFile(pipe, bytes, sizeof(bytes), &length, nullptr) && length) {
        if (error) continue;
        DWORD written;
        if (!WriteFile(output, bytes, length, &written, nullptr)) error = GetLastError();
        else if (written != length) error = ERROR_WRITE_FAULT;
    }
    return error;
}
int child(const char* scenario) {
    const HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD mode;
    if (!GetConsoleMode(output, &mode) || !SetConsoleMode(output, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING)) return 1;
    const char* history = "OLD_PROMPT\r\nROW_1\r\nROW_2\r\nROW_3\r\nROW_4\r\nROW_5\r\n";
    DWORD written;
    if (!WriteFile(output, history, (DWORD)strlen(history), &written, nullptr)) return 2;
    // Make the old screen reach the reader before a subsequent erase; don't rely on render batching.
    Sleep(250);
    const char* clear = !strcmp(scenario, "screen") ? "\x1b[H\x1b[2JNEW_PROMPT" :
        !strcmp(scenario, "history") ? "\x1b[3JNEW_PROMPT" :
        !strcmp(scenario, "alternate") ? "\x1b[?1049h\x1b[H\x1b[2J\x1b[3JALT_SCREEN\x1b[?1049lNEW_PROMPT" :
        "\x1b[H\x1b[2J\x1b[3JNEW_PROMPT";
    if (!WriteFile(output, clear, (DWORD)strlen(clear), &written, nullptr)) return 3;
    Sleep(250);
    return 0;
}
}

// Headless transport fixture: the Zig tests feed stdout through the real terminal parser.
int main(int argc, char** argv) {
    if (argc == 3 && !strcmp(argv[1], "--child")) return child(argv[2]);
    if (argc != 2) return 4;
    if (strcmp(argv[1], "all") && strcmp(argv[1], "screen") && strcmp(argv[1], "history") && strcmp(argv[1], "alternate")) return 5;
    VexConptyApi api = {};
    if (api.load()) return 6;
    HANDLE inputRead = nullptr, inputWrite = nullptr, outputRead = nullptr, outputWrite = nullptr;
    if (!CreatePipe(&inputRead, &inputWrite, nullptr, 0) || !CreatePipe(&outputRead, &outputWrite, nullptr, 0)) return 7;
    HPCON console = nullptr;
    const HRESULT result = api.create(COORD{40, 5}, inputRead, outputWrite, 0, &console);
    CloseHandle(inputRead); CloseHandle(outputWrite);
    if (FAILED(result)) return 8;
    HANDLE reader = CreateThread(nullptr, 0, copyOutput, outputRead, 0, nullptr);
    if (!reader) { CloseHandle(outputRead); api.close(console); return 9; }
    SIZE_T attributeSize = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeSize);
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(malloc(attributeSize));
    if (!attributes || !InitializeProcThreadAttributeList(attributes, 1, 0, &attributeSize)) return 10;
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, sizeof(console), nullptr, nullptr)) return 11;
    wchar_t executable[32768];
    const DWORD length = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    if (!length || length >= ARRAYSIZE(executable)) return 12;
    wchar_t command[32768];
    const int count = swprintf(command, ARRAYSIZE(command), L"\"%ls\" --child %hs", executable, argv[1]);
    if (count < 0 || count >= (int)ARRAYSIZE(command)) return 13;
    STARTUPINFOEXW startup = {};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process = {};
    const BOOL created = CreateProcessW(executable, command, nullptr, nullptr, FALSE, EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &process);
    DeleteProcThreadAttributeList(attributes);
    free(attributes);
    DWORD exitCode = 14;
    if (created) {
        if (WaitForSingleObject(process.hProcess, 15000) != WAIT_OBJECT_0) {
            TerminateProcess(process.hProcess, 15);
            WaitForSingleObject(process.hProcess, INFINITE);
        }
        GetExitCodeProcess(process.hProcess, &exitCode);
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    }
    api.close(console);
    WaitForSingleObject(reader, INFINITE);
    DWORD readerError = ERROR_SUCCESS;
    GetExitCodeThread(reader, &readerError);
    CloseHandle(reader); CloseHandle(outputRead); CloseHandle(inputWrite);
    api.unload();
    return exitCode ? (int)exitCode : readerError ? 16 : 0;
}
