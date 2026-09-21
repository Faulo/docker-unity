#include <windows.h>
#include <shellapi.h>

extern "C" void* memset(void* destination, int value, size_t count)
{
    auto* output = static_cast<volatile unsigned char*>(destination);
    for (size_t index = 0; index < count; ++index)
    {
        output[index] = static_cast<unsigned char>(value);
    }
    return destination;
}

extern "C" void mainCRTStartup()
{
    wchar_t executable[MAX_PATH]{};
    const HINSTANCE findResult = ::FindExecutableW(
        L"C:\\unity-nonexistent.blend",
        nullptr,
        executable);
    if (reinterpret_cast<INT_PTR>(findResult) <= 32 || executable[0] == L'\0')
    {
        ::ExitProcess(10);
    }

    SHELLEXECUTEINFOW executeInfo{};
    executeInfo.cbSize = sizeof(executeInfo);
    executeInfo.fMask = SEE_MASK_NOCLOSEPROCESS;
    executeInfo.lpFile = executable;
    executeInfo.lpParameters = L"--version";
    executeInfo.nShow = SW_HIDE;
    if (!::ShellExecuteExW(&executeInfo) || executeInfo.hProcess == nullptr)
    {
        ::ExitProcess(11);
    }

    const DWORD waitResult = ::WaitForSingleObject(executeInfo.hProcess, 30000);
    DWORD exitCode = 1;
    const BOOL gotExitCode = ::GetExitCodeProcess(executeInfo.hProcess, &exitCode);
    ::CloseHandle(executeInfo.hProcess);
    if (waitResult != WAIT_OBJECT_0 || !gotExitCode || exitCode != 0)
    {
        ::ExitProcess(12);
    }

    ::ExitProcess(0);
}
