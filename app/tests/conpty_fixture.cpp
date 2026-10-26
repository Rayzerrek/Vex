#include "windows_conpty.h"
#include "conpty_graphics_reply.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int testPtyInputQueue();

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
    if (!strcmp(scenario, "appearance-light") || !strcmp(scenario, "appearance-dark")) {
        const bool dark = !strcmp(scenario, "appearance-dark");
        const char* screen = dark
            ? "\x1b[?1049h\x1b[48;2;24;24;24m\x1b[38;2;230;230;230m\x1b[2J\x1b[HStatic RGB palette - no theme protocol\r\n\x1b[38;2;220;70;70mRED  \x1b[38;2;60;180;90mGREEN  \x1b[38;2;80;110;230mBLUE\r\n\x1b[38;2;230;230;230mThe application keeps its original colors."
            : "\x1b[?1049h\x1b[48;2;250;250;250m\x1b[38;2;32;32;32m\x1b[2J\x1b[HStatic RGB palette - no theme protocol\r\n\x1b[38;2;180;40;40mRED  \x1b[38;2;30;130;50mGREEN  \x1b[38;2;50;70;180mBLUE\r\n\x1b[38;2;32;32;32mThe application keeps its original colors.";
        DWORD written;
        if (!WriteFile(output, screen, (DWORD)strlen(screen), &written, nullptr)) return 22;
        const char* highlights = dark
            ? "\r\n\x1b[7m REVERSE VIDEO SELECTION \x1b[27m\r\n\x1b[48;2;230;230;230m\x1b[38;2;24;24;24m EXPLICIT RGB SELECTION \x1b[0m"
            : "\r\n\x1b[7m REVERSE VIDEO SELECTION \x1b[27m\r\n\x1b[48;2;32;32;32m\x1b[38;2;250;250;250m EXPLICIT RGB SELECTION \x1b[0m";
        if (!WriteFile(output, highlights, (DWORD)strlen(highlights), &written, nullptr)) return 23;
        Sleep(30000);
        return 0;
    }
    if (!strcmp(scenario, "kitty-graphics")) {
        const HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
        DWORD inputMode;
        if (!GetConsoleMode(input, &inputMode) || !SetConsoleMode(input,
            (inputMode | ENABLE_VIRTUAL_TERMINAL_INPUT) & ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT))) return 25;
        const char* image = "\x1b_Ga=T,f=24,s=2,v=1,i=42,c=2,r=1;/wAAAP8A\x1b\\\x1b_Ga=q,f=24,s=1,v=1,i=43;/wAA\x1b\\KITTY_TRANSPORT_END";
        DWORD written;
        if (!WriteFile(output, image, (DWORD)strlen(image), &written, nullptr)) return 24;
        const char* expected = "\x1b_Gi=43;OK\x1b\\";
        char reply[32] = {};
        DWORD total = 0;
        while (total < strlen(expected)) {
            DWORD length;
            if (!ReadFile(input, reply + total, (DWORD)strlen(expected) - total, &length, nullptr) || !length) return 26;
            total += length;
        }
        if (memcmp(reply, expected, strlen(expected))) return 27;
        const char* success = "KITTY_REPLY_OK";
        if (!WriteFile(output, success, (DWORD)strlen(success), &written, nullptr)) return 28;
        Sleep(250);
        return 0;
    }
    if (!strcmp(scenario, "color-scheme")) {
        const HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
        DWORD inputMode;
        if (!GetConsoleMode(input, &inputMode) || !SetConsoleMode(input,
            (inputMode | ENABLE_VIRTUAL_TERMINAL_INPUT) & ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT))) return 17;
        const char* report = "\x1b[?2031$p\x1b[?2031h\x1b[?996nCOLOR_SCHEME_PROBE";
        DWORD written;
        if (!WriteFile(output, report, (DWORD)strlen(report), &written, nullptr)) return 2;
        const char* expected = "\x1b[?997;1n";
        char reply[32] = {};
        DWORD total = 0;
        while (total < strlen(expected)) {
            DWORD length;
            if (!ReadFile(input, reply + total, (DWORD)strlen(expected) - total, &length, nullptr) || !length) return 18;
            total += length;
        }
        if (memcmp(reply, expected, strlen(expected))) return 19;
        const char* success = "COLOR_SCHEME_REPLY_OK";
        if (!WriteFile(output, success, (DWORD)strlen(success), &written, nullptr)) return 20;
        Sleep(250);
        return 0;
    }
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

int testKittyImageRenderer();
int testRendererStartup();
int testTabRenameLayout();
int testUpdateSystem();
int testNotificationSurface();
int testUpdatePackage();
int testUpdateDownload();
int testUpdateInstallation();
int testUpdateConcurrency();
int testUpdateDownloadEdges();
int testUpdateHandoffChild();
int testUpdateInstallerParent();
int testUpdateInstallerRestarted();

// Headless transport fixture: the Zig tests feed stdout through the real terminal parser.
int main(int argc, char** argv) {
    if (argc == 1) return testUpdateInstallerRestarted();
    if (argc == 2 && !strcmp(argv[1], "pty-input-queue")) return testPtyInputQueue();
    if (argc == 2 && !strcmp(argv[1], "kitty-renderer")) return testKittyImageRenderer();
    if (argc == 2 && !strcmp(argv[1], "renderer-startup")) return testRendererStartup();
    if (argc == 4 && !strcmp(argv[1], "--update-parent")) return testUpdateInstallerParent();
    if (argc == 2 && !strcmp(argv[1], "--update-hold")) { Sleep(30000); return 0; }
    if (argc == 7 && !strcmp(argv[1], "--apply")) return testUpdateHandoffChild();
    if (argc == 2 && !strcmp(argv[1], "update-concurrency")) return testUpdateConcurrency();
    if (argc == 2 && !strcmp(argv[1], "update-download-edges")) return testUpdateDownloadEdges();
    if (argc == 2 && !strcmp(argv[1], "update-system")) return testUpdateSystem();
    if (argc == 2 && !strcmp(argv[1], "notification-surface")) return testNotificationSurface();
    if (argc == 4 && !strcmp(argv[1], "update-package")) return testUpdatePackage();
    if (argc == 3 && !strcmp(argv[1], "update-download")) return testUpdateDownload();
    if (argc == 3 && !strcmp(argv[1], "update-installation")) return testUpdateInstallation();
    if (argc == 2 && !strcmp(argv[1], "tab-rename-layout")) return testTabRenameLayout();
    if (argc == 3 && !strcmp(argv[1], "--child")) return child(argv[2]);
    if (argc != 2) return 4;
    if (strcmp(argv[1], "all") && strcmp(argv[1], "screen") && strcmp(argv[1], "history") && strcmp(argv[1], "alternate") && strcmp(argv[1], "color-scheme") && strcmp(argv[1], "kitty-graphics")) return 5;
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
        if (!strcmp(argv[1], "color-scheme") || !strcmp(argv[1], "kitty-graphics")) {
            // Let the child enable raw VT input before sending a host notification.
            Sleep(500);
            const char* report = !strcmp(argv[1], "kitty-graphics") ? "\x1b_Gi=43;OK\x1b\\" : "\x1b[?997;1n";
            DWORD written;
            uint8_t encoded[1024];
            const size_t encodedLength = !strcmp(argv[1], "kitty-graphics")
                ? encodeConptyGraphicsReply(reinterpret_cast<const uint8_t*>(report), strlen(report), encoded, sizeof(encoded)) : 0;
            if (!strcmp(argv[1], "kitty-graphics") && !encodedLength) return 29;
            if (!WriteFile(inputWrite, encodedLength ? static_cast<const void*>(encoded) : report,
                (DWORD)(encodedLength ? encodedLength : strlen(report)), &written, nullptr)) return 21;
        }
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
