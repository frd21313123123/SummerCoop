// Native update helper. It runs from TEMP, waits for the original process and
// replaces the EXE in its original location. No scripts or extra runtime DLLs.
static bool UpdateHashValid(const std::wstring &s)
{
    return s.size() == 64 && s.find_first_not_of(L"0123456789abcdefABCDEF") == std::wstring::npos;
}

static std::wstring UpdateFileHash(const std::wstring &path)
{
    HCRYPTPROV provider = 0;
    HCRYPTHASH hash = 0;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return L"";
    bool ok = CryptAcquireContextW(&provider, NULL, NULL, PROV_RSA_AES, CRYPT_VERIFYCONTEXT) &&
              CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash);
    BYTE buffer[65536], digest[32];
    DWORD count = 0, size = sizeof(digest);
    while (ok) {
        if (!ReadFile(file, buffer, sizeof(buffer), &count, NULL)) { ok = false; break; }
        if (!count) break;
        ok = CryptHashData(hash, buffer, count, 0) != FALSE;
    }
    if (ok) ok = CryptGetHashParam(hash, HP_HASHVAL, digest, &size, 0) && size == sizeof(digest);
    if (hash) CryptDestroyHash(hash);
    if (provider) CryptReleaseContext(provider, 0);
    CloseHandle(file);
    std::wstring result;
    if (ok) for (BYTE b : digest) { result += L"0123456789abcdef"[b >> 4]; result += L"0123456789abcdef"[b & 15]; }
    return result;
}

static bool VerifyUpdate(const std::wstring &file, const std::wstring &sha)
{
    return UpdateHashValid(sha) && VerifyPeBinary(file) && !_wcsicmp(UpdateFileHash(file).c_str(), sha.c_str());
}

static std::wstring UpdateQuote(const std::wstring &arg)
{
    std::wstring out = L"\"";
    size_t slashes = 0;
    for (wchar_t c : arg) {
        if (c == L'\\') { ++slashes; continue; }
        out.append(c == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        slashes = 0;
        out += c;
    }
    out.append(slashes * 2, L'\\');
    return out + L'"';
}

static bool UpdateSpawn(const std::wstring &exe, const std::wstring &args, bool inherit, PROCESS_INFORMATION &pi)
{
    STARTUPINFOW si = { sizeof(si) };
    std::wstring cmd = UpdateQuote(exe) + L" " + args;
    std::vector<wchar_t> buffer(cmd.begin(), cmd.end()); buffer.push_back(0);
    return CreateProcessW(exe.c_str(), buffer.data(), NULL, NULL, inherit, CREATE_NO_WINDOW, NULL,
                          DirOf(exe).c_str(), &si, &pi) != FALSE;
}

static std::wstring UpdateTempDir()
{
    wchar_t temp[MAX_PATH] = L"";
    GUID id;
    wchar_t guid[40] = L"";
    if (!GetTempPathW(MAX_PATH, temp) || FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id, guid, 40)) return L"";
    std::wstring dir = std::wstring(temp) + L"SummerCoop-update-" + guid + L"\\";
    return CreateDirectoryW(dir.c_str(), NULL) ? dir : L"";
}

static bool UpdateTempSafe(const std::wstring &dir)
{
    wchar_t temp[MAX_PATH] = L"", full[MAX_PATH] = L"";
    if (!GetTempPathW(MAX_PATH, temp) || !GetFullPathNameW(dir.c_str(), MAX_PATH, full, NULL)) return false;
    std::wstring prefix = std::wstring(temp) + L"SummerCoop-update-{";
    std::wstring path = WithSlash(full);
    return !_wcsnicmp(path.c_str(), prefix.c_str(), prefix.size()) && path.size() == prefix.size() + 38 &&
           path[path.size() - 2] == L'}' && path.find(L'\\', prefix.size()) == path.size() - 1;
}

static void UpdateTempClean(const std::wstring &dir)
{
    if (!UpdateTempSafe(dir)) return;
    for (const wchar_t *name : { L"helper.exe", L"download.exe", L"backup.exe" }) DeleteFileW((dir + name).c_str());
    RemoveDirectoryW(dir.c_str());
}

struct UpdateCleanup { std::wstring dir; HANDLE process; };
static HANDLE g_updateStarted;
static void UpdateSignalStarted()
{
    if (g_updateStarted) { SetEvent(g_updateStarted); CloseHandle(g_updateStarted); g_updateStarted = NULL; }
}
static DWORD WINAPI UpdateCleanupThread(void *value)
{
    UpdateCleanup *p = (UpdateCleanup *)value;
    if (!p->process || WaitForSingleObject(p->process, 60000) == WAIT_OBJECT_0) UpdateTempClean(p->dir);
    if (p->process) CloseHandle(p->process);
    delete p;
    return 0;
}

static bool UpdateReplace(const std::wstring &source, const std::wstring &target)
{
    // The short-lived staging file is on the target volume for atomic rename.
    std::wstring staging = target + L".new";
    if (!CopyFileW(source.c_str(), staging.c_str(), FALSE)) return false;
    for (int attempt = 0; attempt < 100; ++attempt) {
        if (MoveFileExW(staging.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return true;
        Sleep(100);
    }
    DeleteFileW(staging.c_str());
    return false;
}

static void UpdateChecksum(const std::wstring &target)
{
    std::wstring hash = UpdateFileHash(target);
    if (hash.empty()) return;
    std::string text = Narrow(hash, CP_UTF8) + "  " + Narrow(target.substr(target.find_last_of(L"\\/") + 1), CP_UTF8) + "\r\n";
    HANDLE f = CreateFileW((target + L".sha256").c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (f != INVALID_HANDLE_VALUE) { DWORD written; WriteFile(f, text.data(), (DWORD)text.size(), &written, NULL); CloseHandle(f); }
}

// An inherited process handle avoids PID reuse; the ready event is signalled
// only after validation, before the UI is allowed to exit.
static bool UpdateHandoff(const std::wstring &dir, const std::wstring &sha, const std::wstring &restartArgs)
{
    if (!CopyFileW(g_self.c_str(), (dir + L"helper.exe").c_str(), TRUE)) return false;
    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE };
    HANDLE parent = OpenProcess(SYNCHRONIZE, TRUE, GetCurrentProcessId());
    HANDLE ready = CreateEventW(&sa, TRUE, FALSE, NULL);
    PROCESS_INFORMATION pi = {};
    std::wstring args = L"/apply-update " + UpdateQuote(g_self) + L" " + UpdateQuote(sha) + L" " +
                        std::to_wstring((UINT_PTR)parent) + L" " + std::to_wstring((UINT_PTR)ready) + L" " + UpdateQuote(restartArgs);
    bool ok = parent && ready && UpdateSpawn(dir + L"helper.exe", args, true, pi);
    if (ok) {
        HANDLE handles[] = { ready, pi.hProcess };
        ok = WaitForMultipleObjects(2, handles, FALSE, 10000) == WAIT_OBJECT_0;
        if (!ok) TerminateProcess(pi.hProcess, 1);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    }
    if (parent) CloseHandle(parent);
    if (ready) CloseHandle(ready);
    return ok;
}

// Called before game discovery, logging, Steam or UI initialization.
static int UpdateCommand(int argc, wchar_t **argv)
{
    for (int i = 1; i + 1 < argc; ++i) if (!_wcsicmp(argv[i], L"/update-started"))
        g_updateStarted = (HANDLE)(UINT_PTR)_wcstoui64(argv[i + 1], NULL, 10);
    HANDLE cleanupThread = NULL;
    for (int i = 1; i + 2 < argc; ++i) if (!_wcsicmp(argv[i], L"/update-cleanup")) {
        std::wstring dir = WithSlash(argv[i + 2]);
        if (UpdateTempSafe(dir)) {
            UpdateCleanup *p = new UpdateCleanup{ dir, OpenProcess(SYNCHRONIZE, FALSE, wcstoul(argv[i + 1], NULL, 10)) };
            cleanupThread = CreateThread(NULL, 0, UpdateCleanupThread, p, 0, NULL);
            if (!cleanupThread) { if (p->process) CloseHandle(p->process); delete p; }
        }
        break;
    }
    if (argc >= 3 && !_wcsicmp(argv[1], L"/update-probe")) {
        UpdateSignalStarted();
        if (cleanupThread) { WaitForSingleObject(cleanupThread, 15000); CloseHandle(cleanupThread); }
        FILE *report = _wfopen(argv[2], L"w, ccs=UTF-8");
        if (!report) return 1;
        fwprintf(report, L"updated=%s\nsha256=%s\n", g_self.c_str(), UpdateFileHash(g_self).c_str());
        fclose(report);
        return 0;
    }
    if (cleanupThread) CloseHandle(cleanupThread);
    if (argc >= 4 && !_wcsicmp(argv[1], L"/verify-update")) return VerifyUpdate(argv[2], argv[3]) ? 0 : 1;
    if (argc >= 5 && !_wcsicmp(argv[1], L"/test-update-handoff")) {
        std::wstring dir = UpdateTempDir();
        bool ok = !dir.empty() && CopyFileW(argv[2], (dir + L"download.exe").c_str(), TRUE) &&
                  VerifyUpdate(dir + L"download.exe", argv[3]) &&
                  UpdateHandoff(dir, argv[3], L"/update-probe " + UpdateQuote(argv[4]));
        if (!ok) UpdateTempClean(dir);
        return ok ? 0 : 1;
    }
    if (argc >= 7 && !_wcsicmp(argv[1], L"/apply-update")) {
        std::wstring dir = DirOf(g_self), target = argv[2], sha = argv[3];
        HANDLE parent = (HANDLE)(UINT_PTR)_wcstoui64(argv[4], NULL, 10);
        HANDLE ready = (HANDLE)(UINT_PTR)_wcstoui64(argv[5], NULL, 10);
        if (!UpdateTempSafe(dir) || !VerifyUpdate(dir + L"download.exe", sha) || !FileExists(target)) return 1;
        if (!SetEvent(ready)) return 1;
        CloseHandle(ready);
        DWORD wait = WaitForSingleObject(parent, 60000);
        CloseHandle(parent);
        if (wait != WAIT_OBJECT_0) return 1;
        std::wstring backup = dir + L"backup.exe";
        bool saved = CopyFileW(target.c_str(), backup.c_str(), TRUE) != FALSE;
        bool placed = saved && UpdateReplace(dir + L"download.exe", target);
        PROCESS_INFORMATION pi = {};
        std::wstring cleanup = L" /update-cleanup " + std::to_wstring(GetCurrentProcessId()) + L" " + UpdateQuote(dir);
        SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, TRUE };
        HANDLE startedEvent = CreateEventW(&sa, TRUE, FALSE, NULL);
        bool started = placed && startedEvent && UpdateSpawn(target, std::wstring(argv[6]) + cleanup +
                       L" /update-started " + std::to_wstring((UINT_PTR)startedEvent), true, pi);
        if (started) {
            HANDLE handles[] = { startedEvent, pi.hProcess };
            started = WaitForMultipleObjects(2, handles, FALSE, 30000) == WAIT_OBJECT_0;
            if (!started) {
                TerminateProcess(pi.hProcess, 1);
                WaitForSingleObject(pi.hProcess, 5000);
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                pi = {};
            }
        }
        if (startedEvent) CloseHandle(startedEvent);
        bool installed = placed && started;
        if (!started) {
            bool restored = !placed || (saved && UpdateReplace(backup, target));
            if (restored) started = UpdateSpawn(target, std::wstring(argv[6]) + L" /skip-auto-update" + cleanup, false, pi);
        }
        if (started) { UpdateChecksum(target); CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        // Keep the backup in TEMP on a failed recovery for manual restoration.
        return installed ? 0 : 1;
    }
    return -1;
}
