// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 HARMOVUE contributors

#define WIN32_LEAN_AND_MEAN
#define WINVER 0x0602
#define _WIN32_WINNT 0x0602
#include <windows.h>

/* Native loader warmup. No C runtime, exports, UI, or mpv API calls. */
static WCHAR dll_path[32768];
static const WCHAR dll_name[] = L"libmpv-2.dll";

__declspec(noreturn) void WINAPI entry(void)
{
    /* Local to this no-UI helper; retain the inherited process error flags. */
    SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS |
                 SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

    const DWORD capacity = (DWORD)(sizeof(dll_path) / sizeof(dll_path[0]));
    DWORD length = GetModuleFileNameW(NULL, dll_path, capacity);
    DWORD directory_length;
    DWORD index;
    HMODULE module;
    DWORD error;

    if (length == 0) {
        error = GetLastError();
        ExitProcess(error ? error : ERROR_PATH_NOT_FOUND);
    }
    if (length >= capacity) {
        ExitProcess(ERROR_INSUFFICIENT_BUFFER);
    }

    directory_length = length;
    while (directory_length != 0 && dll_path[directory_length - 1] != (WCHAR)0x005c &&
           dll_path[directory_length - 1] != L'/') {
        --directory_length;
    }
    if (directory_length == 0) {
        ExitProcess(ERROR_PATH_NOT_FOUND);
    }
    if (directory_length + (sizeof(dll_name) / sizeof(dll_name[0])) > capacity) {
        ExitProcess(ERROR_INSUFFICIENT_BUFFER);
    }
    for (index = 0; index < (sizeof(dll_name) / sizeof(dll_name[0])); ++index) {
        dll_path[directory_length + index] = dll_name[index];
    }

    module = LoadLibraryExW(dll_path, NULL,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == NULL) {
        error = GetLastError();
        ExitProcess(error ? error : ERROR_DLL_INIT_FAILED);
    }
    if (!FreeLibrary(module)) {
        error = GetLastError();
        ExitProcess(error ? error : ERROR_INVALID_HANDLE);
    }
    ExitProcess(ERROR_SUCCESS);
}
