#pragma once
#include "update_manifest.h"
#include <windows.h>

namespace vex {
inline constexpr wchar_t updateUpgradeCode[] = L"{F3CD9022-815C-4211-AC09-2938D95B26A4}";
struct UpdateInstallation {
    wchar_t directory[MAX_PATH] = {};
    bool machine = false;
};
// Detect only MSI products whose registered install location contains this executable.
bool findUpdateInstallation(const wchar_t* executable, UpdateInstallation& installation);
// Verify byte count, SHA-256, MSI product identity, architecture and version without executing it.
// A successful retainedFile transfers a read-only lease which prevents writes/deletion until closed.
DWORD verifyUpdatePackage(const wchar_t* path, const UpdateManifest& manifest, HANDLE* retainedFile = nullptr, HANDLE cancellation = nullptr);
// New staging directories have a protected ACL for the current user, SYSTEM and administrators.
DWORD createUpdateStage(wchar_t (&directory)[MAX_PATH]);
// Only generated random directory names belong to the updater's stale-cleanup namespace.
bool updateStageNameValid(const wchar_t* name);
// Installed MSI sources are retained: repair/uninstall/rollback may need their embedded cabinet.
bool registeredUpdateSource(const wchar_t* directory);
void removeUpdateStage(const wchar_t* directory);
}
