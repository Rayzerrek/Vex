#define _WIN32_MSI 500
#include "update_package.h"
#include <msi.h>
#include <msiquery.h>
#include <bcrypt.h>
#include <sddl.h>
#include <shlobj.h>
#include <wchar.h>

namespace vex {
static bool installedExecutableMatches(const wchar_t* product, const wchar_t* executable) {
    // Older Vex installers did not register InstallLocation. Resolve their installed file component
    // from Windows Installer's cached database instead of guessing from Program Files or registry names.
    wchar_t cached[MAX_PATH]; DWORD length = MAX_PATH;
    if (MsiGetProductInfoW(product, L"LocalPackage", cached, &length) != ERROR_SUCCESS) return false;
    MSIHANDLE database = 0, view = 0, row = 0;
    if (MsiOpenDatabaseW(cached, nullptr, &database) != ERROR_SUCCESS) return false;
    bool matched = false;
    if (MsiDatabaseOpenViewW(database,
        L"SELECT `File`.`FileName`, `Component`.`ComponentId` FROM `File`, `Component` WHERE `File`.`Component_` = `Component`.`Component`", &view) == ERROR_SUCCESS &&
        MsiViewExecute(view, 0) == ERROR_SUCCESS) {
        const wchar_t* filename = wcsrchr(executable, L'\\');
        if (filename) while (MsiViewFetch(view, &row) == ERROR_SUCCESS) {
            wchar_t name[MAX_PATH], component[80], path[MAX_PATH];
            DWORD nameLength = MAX_PATH, componentLength = 80, pathLength = MAX_PATH;
            if (MsiRecordGetStringW(row, 1, name, &nameLength) == ERROR_SUCCESS && MsiRecordGetStringW(row, 2, component, &componentLength) == ERROR_SUCCESS) {
                const wchar_t* longName = wcschr(name, L'|');
                if (!_wcsicmp(longName ? longName + 1 : name, filename + 1) &&
                    MsiGetComponentPathW(product, component, path, &pathLength) == INSTALLSTATE_LOCAL && !_wcsicmp(path, executable)) matched = true;
            }
            MsiCloseHandle(row); row = 0;
            if (matched) break;
        }
    }
    if (view) MsiCloseHandle(view);
    MsiCloseHandle(database);
    return matched;
}
bool findUpdateInstallation(const wchar_t* executable, UpdateInstallation& installation) {
    wchar_t current[MAX_PATH];
    if (wcslen(executable) >= MAX_PATH) return false;
    wcscpy(current, executable);
    wchar_t* slash = wcsrchr(current, L'\\');
    if (!slash) return false;
    slash[1] = 0;
    for (DWORD index = 0;; ++index) {
        wchar_t product[39];
        if (MsiEnumRelatedProductsW(updateUpgradeCode, 0, index, product) != ERROR_SUCCESS) return false;
        wchar_t location[MAX_PATH];
        DWORD length = MAX_PATH;
        if (MsiGetProductInfoW(product, L"InstallLocation", location, &length) != ERROR_SUCCESS || !length || length >= MAX_PATH - 1) {
            if (!installedExecutableMatches(product, executable)) continue;
            wcscpy(location, current);
        } else {
            if (location[length - 1] != L'\\') { location[length++] = L'\\'; location[length] = 0; }
            if (_wcsicmp(location, current)) continue;
        }
        wchar_t assignment[8];
        length = 8;
        if (MsiGetProductInfoW(product, L"AssignmentType", assignment, &length) != ERROR_SUCCESS) return false;
        if (wcscmp(assignment, L"0") && wcscmp(assignment, L"1")) return false;
        wcscpy(installation.directory, location);
        installation.machine = !wcscmp(assignment, L"1");
        return true;
    }
}
static bool msiProperty(MSIHANDLE database, const wchar_t* name, wchar_t (&value)[80]) {
    wchar_t query[180];
    swprintf(query, 180, L"SELECT `Value` FROM `Property` WHERE `Property`='%ls'", name);
    MSIHANDLE view = 0, record = 0;
    bool success = MsiDatabaseOpenViewW(database, query, &view) == ERROR_SUCCESS &&
        MsiViewExecute(view, 0) == ERROR_SUCCESS && MsiViewFetch(view, &record) == ERROR_SUCCESS;
    DWORD length = 80;
    if (success) success = MsiRecordGetStringW(record, 1, value, &length) == ERROR_SUCCESS;
    if (record) MsiCloseHandle(record);
    if (view) MsiCloseHandle(view);
    return success;
}
DWORD verifyUpdatePackage(const wchar_t* path, const UpdateManifest& manifest, HANDLE* retainedFile, HANDLE cancellation) {
    if (retainedFile) *retainedFile = INVALID_HANDLE_VALUE;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError();
    BY_HANDLE_FILE_INFORMATION info = {};
    DWORD error = ERROR_SUCCESS;
    if (!GetFileInformationByHandle(file, &info)) error = GetLastError();
    else if ((info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) ||
        (uint64_t(info.nFileSizeHigh) << 32 | info.nFileSizeLow) != manifest.size) error = ERROR_INVALID_DATA;
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (!error && BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) error = ERROR_INVALID_DATA;
    if (!error && BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) error = ERROR_INVALID_DATA;
    uint8_t bytes[65536];
    while (!error) {
        if (cancellation && WaitForSingleObject(cancellation, 0) == WAIT_OBJECT_0) { error = ERROR_CANCELLED; break; }
        DWORD length;
        if (!ReadFile(file, bytes, sizeof(bytes), &length, nullptr)) { error = GetLastError(); break; }
        if (!length) break;
        if (BCryptHashData(hash, bytes, length, 0) < 0) error = ERROR_INVALID_DATA;
    }
    uint8_t digest[32];
    if (!error && (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0 || memcmp(digest, manifest.sha256, sizeof(digest)))) error = ERROR_CRC;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    // Keep the file read-locked through MSI inspection; neither verification nor inspection runs custom actions.
    if (!error) {
        MSIHANDLE database = 0;
        error = MsiOpenDatabaseW(path, nullptr, &database);
        if (!error) {
            wchar_t upgrade[80], version[80], expected[80];
            swprintf(expected, 80, L"%u.%u.%u.0", manifest.version.parts[0], manifest.version.parts[1], manifest.version.parts[2]);
            if (!msiProperty(database, L"UpgradeCode", upgrade) || _wcsicmp(upgrade, updateUpgradeCode) ||
                !msiProperty(database, L"ProductVersion", version) || wcscmp(version, expected)) error = ERROR_INVALID_DATA;
            MSIHANDLE summary = 0;
            if (!error) {
                error = MsiGetSummaryInformationW(database, nullptr, 0, &summary);
                if (!error) {
                    wchar_t platform[80] = {}; DWORD length = 80; UINT type = 0; INT number = 0; FILETIME time = {};
                    error = MsiSummaryInfoGetPropertyW(summary, 7, &type, &number, &time, platform, &length);
                    if (!error && wcsncmp(platform, L"x64;", 4)) error = ERROR_INVALID_DATA;
                    MsiCloseHandle(summary);
                }
            }
            MsiCloseHandle(database);
        }
    }
    if (!error && retainedFile) *retainedFile = file;
    else CloseHandle(file);
    return error;
}
DWORD createUpdateStage(wchar_t (&directory)[MAX_PATH]) {
    wchar_t local[MAX_PATH];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, local))) return ERROR_PATH_NOT_FOUND;
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return GetLastError();
    alignas(TOKEN_USER) uint8_t user[1024]; DWORD length = 0;
    const BOOL gotUser = GetTokenInformation(token, TokenUser, user, sizeof(user), &length);
    const DWORD tokenError = gotUser ? ERROR_SUCCESS : GetLastError();
    CloseHandle(token);
    if (!gotUser) return tokenError;
    wchar_t* sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user)->User.Sid, &sid)) return GetLastError();
    wchar_t sddl[512];
    swprintf(sddl, 512, L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;%ls)", sid);
    LocalFree(sid);
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, nullptr)) return GetLastError();
    SECURITY_ATTRIBUTES security = {sizeof(security), descriptor, FALSE};
    uint8_t random[16];
    DWORD error = ERROR_SUCCESS;
    if (BCryptGenRandom(nullptr, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0) error = ERROR_GEN_FAILURE;
    if (!error) {
        wchar_t suffix[33];
        for (unsigned i = 0; i < 16; ++i) swprintf(suffix + i * 2, 3, L"%02x", random[i]);
        const int count = swprintf(directory, MAX_PATH, L"%ls\\Vex-Update-%ls", local, suffix);
        if (count < 0 || count >= MAX_PATH) error = ERROR_FILENAME_EXCED_RANGE;
        else if (!CreateDirectoryW(directory, &security)) error = GetLastError();
    }
    LocalFree(descriptor);
    if (error) directory[0] = 0;
    return error;
}
bool updateStageNameValid(const wchar_t* name) {
    constexpr wchar_t prefix[] = L"Vex-Update-";
    constexpr size_t prefixLength = sizeof(prefix) / sizeof(wchar_t) - 1;
    if (wcslen(name) != prefixLength + 32 || wcsncmp(name, prefix, prefixLength)) return false;
    for (const wchar_t* at = name + prefixLength; *at; ++at) if (!(*at >= L'0' && *at <= L'9') && !(*at >= L'a' && *at <= L'f')) return false;
    return true;
}
bool registeredUpdateSource(const wchar_t* directory) {
    wchar_t expected[MAX_PATH];
    const int length = swprintf(expected, MAX_PATH, L"%ls\\", directory);
    if (length < 0 || length >= MAX_PATH) return true;
    for (DWORD index = 0;; ++index) {
        wchar_t product[39];
        const UINT result = MsiEnumRelatedProductsW(updateUpgradeCode, 0, index, product);
        if (result == ERROR_NO_MORE_ITEMS) return false;
        if (result != ERROR_SUCCESS) return true; // An uncertain registry query must not evict recovery data.
        wchar_t source[MAX_PATH]; DWORD count = MAX_PATH;
        if (MsiGetProductInfoW(product, L"InstallSource", source, &count) != ERROR_SUCCESS) return true;
        if (CompareStringOrdinal(source, -1, expected, -1, TRUE) == CSTR_EQUAL) return true;
    }
}
void removeUpdateStage(const wchar_t* directory) {
    if (!directory[0]) return;
    const wchar_t* names[] = {L"update.msi", L"update.part", L"vex-update.exe", L"manifest.txt", L"install.log"};
    for (const auto* name : names) {
        wchar_t path[MAX_PATH];
        if (swprintf(path, MAX_PATH, L"%ls\\%ls", directory, name) > 0) DeleteFileW(path);
    }
    RemoveDirectoryW(directory);
}
}
