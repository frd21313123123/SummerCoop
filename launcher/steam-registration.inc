// Keep Steam's My Summer Car binding on one current launcher. Steam can cache
// launch options while open, so remembered legacy targets receive the same EXE.
#include "steam-binding.inc"

struct SteamRegistration {
    std::wstring launcher;
    int configs = 0, aliases = 0, failed = 0;
};

static std::wstring SteamBindingId()
{
    GUID id;
    wchar_t text[40] = L"";
    if (FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, text, _countof(text))) return L"";
    return text;
}

static std::wstring SteamBindingRead(const std::wstring &file, const wchar_t *section, const wchar_t *key)
{
    std::vector<wchar_t> value(32768);
    GetPrivateProfileStringW(section, key, L"", value.data(), (DWORD)value.size(), file.c_str());
    return value.data();
}

static bool SteamBindingWrite(const std::wstring &file, const wchar_t *section, const wchar_t *key, const std::wstring &value)
{
    if (SteamBindingRead(file, section, key) == value) return true;
    return WritePrivateProfileStringW(section, key, value.c_str(), file.c_str()) != FALSE;
}

static std::wstring SteamBindingFile()
{
    wchar_t local[MAX_PATH] = L"";
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", local, _countof(local))) return L"";
    std::wstring dir = std::wstring(local) + L"\\SummerCoop\\";
    return dir + L"steam-launcher.ini";
}

static void SteamBindingAdd(std::vector<std::wstring> &paths, const std::wstring &value)
{
    std::wstring path = steam_binding::FullPath(value);
    if (path.empty() || !steam_binding::LauncherName(path) || paths.size() >= 32) return;
    for (const std::wstring &other : paths) if (steam_binding::SamePath(path, other)) return;
    paths.push_back(path);
}

static int SteamBindingCopy(const std::wstring &source, const std::wstring &target, const std::wstring &sha)
{
    if (steam_binding::SamePath(source, target)) return 0;
    unsigned long long theirs = ExeVersion(target), mine = ExeVersion(source);
    // Only recognized launcher paths taken from this game's binding are copied.
    // Never replace an unknown executable or a more recent build.
    if (!mine || !steam_binding::LauncherName(target) || (FileExists(target) && !theirs) || theirs > mine) return -1;
    if (!_wcsicmp(UpdateFileHash(target).c_str(), sha.c_str())) return 0;
    std::wstring id = SteamBindingId();
    if (id.empty()) return -1;
    std::wstring staging = target + L".summercoop-" + id + L".tmp";
    bool ok = CopyFileW(source.c_str(), staging.c_str(), TRUE) && VerifyUpdate(staging, sha) &&
              MoveFileExW(staging.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    DeleteFileW(staging.c_str());
    if (!ok) return -1;
    UpdateChecksum(target);
    return 1;
}

static bool SteamBindingReadText(HANDLE file, std::string &text)
{
    LARGE_INTEGER size = {};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > (16u << 20)) return false;
    text.resize((size_t)size.QuadPart);
    DWORD read = 0;
    return ReadFile(file, &text[0], (DWORD)text.size(), &read, NULL) && read == text.size();
}

static bool SteamBindingSave(const std::wstring &file, const std::string &text)
{
    std::wstring id = SteamBindingId();
    if (id.empty()) return false;
    std::wstring staging = file + L".summercoop-" + id + L".tmp";
    HANDLE output = CreateFileW(staging.c_str(), GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (output == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    bool ok = WriteFile(output, text.data(), (DWORD)text.size(), &written, NULL) && written == text.size() && FlushFileBuffers(output);
    CloseHandle(output);
    // Preserve the first original alongside Steam's configuration, outside the
    // standalone launcher folder. ReplaceFile preserves the configuration ACL.
    std::wstring backup = file + L".summercoop.bak";
    if (ok && !FileExists(backup)) ok = CopyFileW(file.c_str(), backup.c_str(), TRUE) != FALSE;
    if (ok) ok = ReplaceFileW(file.c_str(), staging.c_str(), NULL, 0, NULL, NULL) != FALSE;
    DeleteFileW(staging.c_str());
    return ok;
}

static std::vector<std::wstring> SteamBindingConfigs(const std::vector<std::wstring> &roots)
{
    std::vector<std::wstring> files;
    for (const std::wstring &root : roots) {
        WIN32_FIND_DATAW data;
        std::wstring users = WithSlash(root) + L"userdata\\";
        HANDLE find = FindFirstFileW((users + L"*").c_str(), &data);
        if (find == INVALID_HANDLE_VALUE) continue;
        do {
            std::wstring name = data.cFileName;
            if (!(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || name.empty() || name.find_first_not_of(L"0123456789") != std::wstring::npos) continue;
            std::wstring path = users + name + L"\\config\\localconfig.vdf";
            if (!FileExists(path)) continue;
            bool duplicate = false;
            for (const std::wstring &other : files) if (steam_binding::SamePath(other, path)) duplicate = true;
            if (!duplicate) files.push_back(path);
        } while (FindNextFileW(find, &data));
        FindClose(find);
    }
    return files;
}

static SteamRegistration SteamRegister(const std::vector<std::wstring> &roots, const std::wstring &stateFile,
                                      const std::wstring &gameLauncher = L"")
{
    SteamRegistration result;
    result.launcher = steam_binding::FullPath(g_self);
    if (stateFile.empty() || result.launcher.empty() || !steam_binding::LauncherName(result.launcher)) return result;
    // Serialize two launcher copies starting at once; do not wait for Steam.
    HANDLE mutex = CreateMutexW(NULL, FALSE, L"Local\\SummerCoop-SteamBinding");
    DWORD wait = mutex ? WaitForSingleObject(mutex, 3000) : WAIT_FAILED;
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) { if (mutex) CloseHandle(mutex); return result; }
    struct Unlock { HANDLE mutex; ~Unlock() { ReleaseMutex(mutex); CloseHandle(mutex); } } unlock{ mutex };
    std::vector<std::wstring> aliases;
    int count = min(32, (int)GetPrivateProfileIntW(L"SteamAliases", L"Count", 0, stateFile.c_str()));
    for (int i = 0; i < count; ++i) SteamBindingAdd(aliases, SteamBindingRead(stateFile, L"SteamAliases", std::to_wstring(i).c_str()));
    if (FileExists(gameLauncher)) SteamBindingAdd(aliases, gameLauncher);
    struct Config { std::wstring path; std::string text; HANDLE file; };
    std::vector<Config> configs;
    struct CloseConfigs { std::vector<Config> &configs; ~CloseConfigs() { for (const Config &c : configs) CloseHandle(c.file); } } closeConfigs{ configs };
    for (const std::wstring &config : SteamBindingConfigs(roots)) {
        // Deny concurrent writes while reading and replacing this file. An
        // active writer makes us defer; we never overwrite its in-flight data.
        HANDLE input = CreateFileW(config.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
                                   NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (input == INVALID_HANDLE_VALUE) { ++result.failed; continue; }
        std::string text;
        if (!SteamBindingReadText(input, text)) { CloseHandle(input); ++result.failed; continue; }
        steam_binding::Patch patch = steam_binding::Rewrite(text, result.launcher);
        if (!patch.valid) ++result.failed;
        if (patch.supported) SteamBindingAdd(aliases, patch.oldPath);
        configs.push_back({ config, text, input });
    }
    std::wstring registered = steam_binding::FullPath(SteamBindingRead(stateFile, L"Launcher", L"Path"));
    if (!steam_binding::LauncherName(registered) || !ExeVersion(registered)) registered.clear();
    SteamBindingAdd(aliases, registered);
    SteamBindingAdd(aliases, result.launcher);
    std::wstring source = registered.empty() ? result.launcher : registered;
    for (const std::wstring &candidate : { result.launcher, gameLauncher })
        if (steam_binding::LauncherName(candidate) && ExeVersion(candidate) > ExeVersion(source)) source = candidate;
    for (const std::wstring &candidate : aliases)
        if (ExeVersion(candidate) > ExeVersion(source)) source = candidate;
    if (!ExeVersion(source)) return result;
    std::wstring sha = UpdateFileHash(source);
    if (!UpdateHashValid(sha)) { ++result.failed; return result; }
    std::wstring preferred = registered.empty() ? result.launcher : registered;
    if (!steam_binding::SamePath(source, preferred)) {
        int copied = SteamBindingCopy(source, preferred, sha);
        if (copied >= 0) result.aliases += copied;
        else { ++result.failed; preferred = source; }
    }
    result.launcher = preferred;
    // Remember a newer copy that was moved into the preferred path as well.
    if (!steam_binding::SamePath(source, preferred)) SteamBindingAdd(aliases, source);
    for (const Config &config : configs) {
        steam_binding::Patch patch = steam_binding::Rewrite(config.text, preferred);
        if (patch.supported) {
            if (patch.text != config.text) {
                if (SteamBindingSave(config.path, patch.text)) ++result.configs;
                else ++result.failed;
            }
        }
    }
    for (const std::wstring &alias : aliases) {
        int copied = SteamBindingCopy(source, alias, sha);
        if (copied >= 0) result.aliases += copied;
        else ++result.failed;
    }
    if (SHCreateDirectoryExW(NULL, DirOf(stateFile).c_str(), NULL) == ERROR_SUCCESS || FileExists(stateFile) ||
        GetFileAttributesW(DirOf(stateFile).c_str()) != INVALID_FILE_ATTRIBUTES) {
        if (!FileExists(stateFile)) {
            // Profile APIs retain Unicode paths when the file has a UTF-16 BOM.
            HANDLE file = CreateFileW(stateFile.c_str(), GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
            if (file != INVALID_HANDLE_VALUE) { WORD bom = 0xFEFF; DWORD written; WriteFile(file, &bom, sizeof(bom), &written, NULL); CloseHandle(file); }
        }
        if (!SteamBindingWrite(stateFile, L"Launcher", L"Path", preferred)) ++result.failed;
        for (size_t i = 0; i < aliases.size(); ++i)
            if (!SteamBindingWrite(stateFile, L"SteamAliases", std::to_wstring(i).c_str(), aliases[i])) ++result.failed;
        if (!SteamBindingWrite(stateFile, L"SteamAliases", L"Count", std::to_wstring(aliases.size()))) ++result.failed;
    } else ++result.failed;
    return result;
}

static SteamRegistration SteamRegisterInstalled()
{
    std::vector<std::wstring> roots;
    SteamLibraries(roots);
    return SteamRegister(roots, SteamBindingFile(), g_gameDir.empty() ? L"" : g_gameDir + L"SummerCoop-Launcher.exe");
}

static int SteamRegistrationCommand(int argc, wchar_t **argv)
{
    bool test = argc >= 5 && !_wcsicmp(argv[1], L"/test-steam-binding");
    bool install = argc >= 2 && !_wcsicmp(argv[1], L"/register-steam");
    if (!test && !install) return -1;
    // Tests supply every path; they cannot discover the user's Steam or game.
    SteamRegistration result = test ? SteamRegister({ WithSlash(argv[2]) }, argv[3]) : SteamRegisterInstalled();
    const wchar_t *report = test ? argv[4] : argc >= 3 ? argv[2] : NULL;
    if (report) {
        FILE *file = _wfopen(report, L"w, ccs=UTF-8");
        if (!file) return 1;
        fwprintf(file, L"launcher=%s\nconfigs_updated=%d\naliases_updated=%d\nfailures=%d\n",
                 result.launcher.c_str(), result.configs, result.aliases, result.failed);
        fclose(file);
    }
    return result.failed ? 1 : 0;
}
