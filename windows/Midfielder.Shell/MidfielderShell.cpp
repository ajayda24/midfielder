// Windows 11 top-level Explorer context-menu entry "Send to phone" (IExplorerCommand).
// Loaded by Explorer through the sparse package's COM surrogate. It does almost nothing itself: it writes the
// selected paths to a temp file and starts Midfielder.exe --send-list <file>; the running app (if any) takes
// them over its pipe and sends them in one batch.

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/module.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <string>

using namespace Microsoft::WRL;

namespace
{
    std::wstring ModuleDir()
    {
        HMODULE module = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&ModuleDir), &module);
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(module, path, MAX_PATH);
        PathRemoveFileSpecW(path);
        return path;
    }

    std::wstring ExePath() { return ModuleDir() + L"\\Midfielder.exe"; }

    bool WriteUtf8(const std::wstring& file, const std::wstring& text)
    {
        int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(static_cast<size_t>(bytes), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), utf8.data(), bytes, nullptr, nullptr);
        HANDLE h = CreateFileW(file.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;
        DWORD written = 0;
        BOOL ok = WriteFile(h, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(h);
        return ok && written == utf8.size();
    }
}

class __declspec(uuid("5009E094-7566-4276-BCE2-E254ADA449BC")) SendToPhoneCommand final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand, IObjectWithSite>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override { return SHStrDupW(L"Send to phone", name); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        std::wstring location = ExePath() + L",0";
        return SHStrDupW(location.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        *guid = __uuidof(SendToPhoneCommand);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (items == nullptr) return S_OK;
        DWORD count = 0;
        if (FAILED(items->GetCount(&count))) return S_OK;
        std::wstring list;
        for (DWORD i = 0; i < count; i++)
        {
            ComPtr<IShellItem> item;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
            {
                list += path;
                list += L"\n";
                CoTaskMemFree(path);
            }
        }
        if (list.empty()) return S_OK;

        wchar_t dir[MAX_PATH] = {};
        wchar_t file[MAX_PATH] = {};
        if (GetTempPathW(MAX_PATH, dir) == 0 || GetTempFileNameW(dir, L"mfs", 0, file) == 0) return S_OK;
        if (!WriteUtf8(file, list)) return S_OK;

        std::wstring command = L"\"" + ExePath() + L"\" --send-list \"" + file + L"\"";
        STARTUPINFOW si = {sizeof(si)};
        PROCESS_INFORMATION pi = {};
        if (CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        return S_OK;
    }

    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        _site = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** site) override { return _site.CopyTo(riid, site); }

private:
    ComPtr<IUnknown> _site;
};

CoCreatableClass(SendToPhoneCommand)

STDAPI DllCanUnloadNow()
{
    return Module<ModuleType::InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv)
{
    return Module<ModuleType::InProc>::GetModule().GetClassObject(rclsid, riid, ppv);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
