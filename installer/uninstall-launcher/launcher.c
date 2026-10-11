#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <wchar.h>

#define PATH_CAPACITY 32768

static int ReportError(DWORD error, const wchar_t *commandTail)
{
    if (!wcsstr(commandTail, L"/SUPPRESSMSGBOXES"))
        MessageBoxW(NULL, L"无法启动轻截卸载程序。请重新安装轻截以修复卸载文件，再重试。",
                    L"卸载轻截 QuickCapture", MB_OK | MB_ICONERROR);
    return error ? (int)error : ERROR_BAD_CONFIGURATION;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandTail, int show)
{
    (void)instance;
    (void)previous;
    wchar_t directory[PATH_CAPACITY], iniPath[PATH_CAPACITY], executable[PATH_CAPACITY];
    wchar_t engineName[64], command[PATH_CAPACITY];
    DWORD length = GetModuleFileNameW(NULL, directory, PATH_CAPACITY);
    if (!length || length >= PATH_CAPACITY)
        return ReportError(ERROR_BAD_PATHNAME, commandTail);
    wchar_t *separator = wcsrchr(directory, L'\\');
    if (!separator)
        return ReportError(ERROR_BAD_PATHNAME, commandTail);
    separator[1] = L'\0';
    size_t directoryLength = wcslen(directory);
    if (directoryLength + 64 >= PATH_CAPACITY)
        return ReportError(ERROR_BAD_PATHNAME, commandTail);
    wcscpy(iniPath, directory);
    wcscat(iniPath, L"uninsQuickCapture.ini");
    GetPrivateProfileStringW(L"Uninstall", L"Executable", L"", engineName, 64, iniPath);
    // Only launch the Inno engine in this installation, never an arbitrary INI path.
    if (wcslen(engineName) != 12 || _wcsnicmp(engineName, L"unins", 5) ||
        engineName[5] < L'0' || engineName[5] > L'9' ||
        engineName[6] < L'0' || engineName[6] > L'9' ||
        engineName[7] < L'0' || engineName[7] > L'9' ||
        _wcsicmp(engineName + 8, L".exe"))
        return ReportError(ERROR_BAD_CONFIGURATION, commandTail);
    wcscpy(executable, directory);
    wcscat(executable, engineName);
    if (GetFileAttributesW(executable) == INVALID_FILE_ATTRIBUTES)
        return ReportError(GetLastError(), commandTail);
    if (wcslen(executable) + wcslen(commandTail) + 4 >= PATH_CAPACITY)
        return ReportError(ERROR_BAD_ARGUMENTS, commandTail);
    swprintf(command, PATH_CAPACITY, L"\"%ls\" %ls", executable, commandTail);
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION process = {0};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = (WORD)show;
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, directory, &startup, &process))
        return ReportError(GetLastError(), commandTail);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    // Exit immediately so the engine can delete this launcher during uninstall.
    return 0;
}
