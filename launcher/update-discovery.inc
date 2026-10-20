// Discovery and news are separate. Stable binaries use the documented public
// release redirect; GitHub's REST quota can only postpone history/nightly reads.
#include <mutex>
static DWORD g_relCode, g_relErr;
static bool g_relRateLimited;
static long long g_relRetryAt;
static DWORD g_stableCode;
static bool g_stableHasTag;
static std::wstring g_updateCacheOverride;
static std::mutex g_releaseFetchMutex;
typedef bool (*UpdateFetchFn)(const std::wstring &, std::string *, DWORD *, HttpReply *, const std::wstring &, bool);
static UpdateFetchFn g_updateFetchFixture;
static bool UpdateFetch(const std::wstring &url, std::string *body, DWORD *code, HttpReply *reply,
                        const std::wstring &etag = L"", bool follow = true)
{
    if (g_updateFetchFixture) return g_updateFetchFixture(url, body, code, reply, etag, follow);
    return HttpGet(url, body, L"", false, code, 0, 8u << 20, reply, etag, follow);
}
static std::wstring UpdateCachePath(const wchar_t *extension)
{
    std::wstring directory = g_updateCacheOverride;
    if (directory.empty()) {
        wchar_t local[MAX_PATH] = {}; GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
        if (!local[0]) return L""; directory = std::wstring(local) + L"\\SummerCoop";
    }
    CreateDirectoryW(directory.c_str(), NULL);
    std::wstring key = g_repo;
    for (wchar_t &c : key) if (!(iswalnum(c) || c == L'-' || c == L'.')) c = L'_';
    return WithSlash(directory) + L"launcher-releases-" + key + extension;
}
static std::string ReleaseCache()
{
    std::vector<unsigned char> data;
    WIN32_FILE_ATTRIBUTE_DATA info = {}; std::wstring path = UpdateCachePath(L".json");
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &info) && !info.nFileSizeHigh && info.nFileSizeLow <= (8u << 20)
        && ReadAll(path, data) && data.size() <= (8u << 20)) return std::string(data.begin(), data.end());
    return "";
}
static long long CacheNumber(const wchar_t *key)
{
    wchar_t value[64] = {}; GetPrivateProfileStringW(L"HTTP", key, L"0", value, _countof(value), UpdateCachePath(L".ini").c_str());
    return HeaderNumber(value);
}
static void CacheNumber(const wchar_t *key, long long value)
{ WritePrivateProfileStringW(L"HTTP", key, std::to_wstring(value).c_str(), UpdateCachePath(L".ini").c_str()); }
static bool ApiLimited(const HttpReply &reply, const std::string &body)
{ return reply.status == 429 || reply.status == 403 && (reply.remaining == 0 || body.find("rate limit") != std::string::npos); }
static bool EmptyReleaseList(const std::string &body)
{ std::string compact; for (char c : body) if (c != ' ' && c != '\r' && c != '\n' && c != '\t') compact += c; return compact == "[]"; }
static long long ApiRetryAt(const HttpReply &reply, long long now, int failures)
{
    long long until = reply.retryAt;
    if (reply.remaining == 0) until = std::max(until, reply.resetAt);
    return until > now ? until : now + std::min(1800LL, 60LL << std::min(5, std::max(0, failures)));
}
static int FetchReleases(std::string &json)
{
    std::lock_guard<std::mutex> lock(g_releaseFetchMutex);
    // Coordinate launcher copies too: they share the same on-disk quota and
    // metadata, even if one EXE was started from an older download directory.
    struct Lease {
        HANDLE handle = NULL; bool held = false;
        Lease() {
            std::wstring name = g_repo; for (wchar_t &c : name) if (!iswalnum(c)) c = L'_';
            handle = CreateMutexW(NULL, FALSE, (L"Local\\SummerCoop.ReleaseMetadata." + name).c_str());
            if (handle) { DWORD wait = WaitForSingleObject(handle, 60000); held = wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED; }
        }
        ~Lease() { if (held) ReleaseMutex(handle); if (handle) CloseHandle(handle); }
    } lease;
    if (!lease.held) { json = ReleaseCache(); g_relCode = 0; g_relErr = ERROR_TIMEOUT; g_relRateLimited = false; return REL_OFFLINE; }
    long long now = (long long)_time64(NULL), checked = CacheNumber(L"Checked"), retry = CacheNumber(L"RetryAt");
    json = ReleaseCache();
    if (!json.empty() && ParseReleases(json).empty() && !EmptyReleaseList(json)) json.clear();
    g_relRetryAt = retry; g_relRateLimited = retry > now;
    if (retry > now) { g_relCode = 429; return REL_OFFLINE; }
    if (!json.empty() && checked <= now && now - checked < 1800) { g_relCode = 200; g_relErr = 0; return EmptyReleaseList(json) ? REL_NONE : REL_OK; }
    wchar_t tag[512] = {}; GetPrivateProfileStringW(L"HTTP", L"ETag", L"", tag, _countof(tag), UpdateCachePath(L".ini").c_str());
    // INI profile reads strip enclosing quotes, which are part of an HTTP ETag.
    std::wstring conditionalTag = tag;
    if (conditionalTag.compare(0, 2, L"V:") == 0) conditionalTag.erase(0, 2);
    std::string body; DWORD code = 0; HttpReply reply;
    bool got = UpdateFetch(L"https://api.github.com/repos/" + g_repo + L"/releases?per_page=100", &body, &code, &reply, json.empty() ? L"" : conditionalTag);
    g_relCode = code; g_relErr = got ? 0 : g_httpErr; g_relRateLimited = ApiLimited(reply, body);
    if (g_relRateLimited) {
        int failures = (int)std::min(5LL, CacheNumber(L"Failures"));
        g_relRetryAt = ApiRetryAt(reply, now, failures); CacheNumber(L"RetryAt", g_relRetryAt); CacheNumber(L"Failures", failures + 1);
    }
    LaunchLog("release metadata: HTTP %lu, WinHTTP %lu, limited=%d retry=%lld", code, g_relErr, (int)g_relRateLimited, g_relRetryAt);
    if (got && code == 304 && !json.empty()) {
        CacheNumber(L"Checked", now); CacheNumber(L"RetryAt", 0); CacheNumber(L"Failures", 0); g_relRetryAt = 0;
        return EmptyReleaseList(json) ? REL_NONE : REL_OK;
    }
    if (got && code == 200 && (!ParseReleases(body).empty() || EmptyReleaseList(body))) {
        std::wstring path = UpdateCachePath(L".json"), tmp = path + L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
        bool stored = false;
        HANDLE file = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
        if (file != INVALID_HANDLE_VALUE) {
            DWORD written; bool ok = WriteFile(file, body.data(), (DWORD)body.size(), &written, NULL) && written == body.size(); CloseHandle(file);
            if (ok) stored = MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING) != FALSE;
            if (!stored) DeleteFileW(tmp.c_str());
        }
        if (stored) {
            WritePrivateProfileStringW(L"HTTP", L"ETag", (L"V:" + reply.etag).c_str(), UpdateCachePath(L".ini").c_str());
            CacheNumber(L"Checked", now);
        }
        CacheNumber(L"RetryAt", 0); CacheNumber(L"Failures", 0);
        json = body; g_relRetryAt = 0; return EmptyReleaseList(body) ? REL_NONE : REL_OK;
    }
    if (code == 404) { json.clear(); return REL_NONE; }
    return REL_OFFLINE;
}
static bool StableTag(const std::wstring &location, std::wstring &tag, std::wstring &version)
{
    const std::wstring prefix = L"https://github.com/" + g_repo + L"/releases/tag/";
    if (location.compare(0, prefix.size(), prefix)) return false;
    tag = location.substr(prefix.size()); version = tag;
    if (!version.empty() && version[0] == L'v') version.erase(0, 1);
    if (version.empty() || version.find_first_not_of(L"0123456789.") != std::wstring::npos) return false;
    size_t start = 0; int parts = 0;
    while (start < version.size()) {
        size_t end = version.find(L'.', start); if (end == std::wstring::npos) end = version.size();
        if (end == start || HeaderNumber(version.substr(start, end - start)) > 65535) return false;
        parts++; start = end + 1;
    }
    return (parts == 3 || parts == 4) && version.back() != L'.';
}
static bool StableRelease(Note &release)
{
    g_stableCode = 0; g_stableHasTag = false;
    DWORD code = 0; HttpReply reply; std::string body;
    UpdateFetch(L"https://github.com/" + g_repo + L"/releases/latest", &body, &code, &reply, L"", false);
    g_stableCode = code;
    std::wstring tag, version;
    if (!(code == 301 || code == 302 || code == 303 || code == 307 || code == 308) || !StableTag(reply.location, tag, version)) return false;
    g_stableHasTag = true;
    const std::wstring root = L"https://github.com/" + g_repo + L"/releases/download/" + tag + L"/";
    std::string checksum;
    bool got = UpdateFetch(root + L"SummerCoop-Launcher.exe.sha256", &checksum, &code, &reply); g_stableCode = code;
    if (!got) return false;
    std::wstring hash = UpdateChecksumHash(checksum); if (hash.empty()) return false;
    release.ver = version; release.page = L"https://github.com/" + g_repo + L"/releases/tag/" + tag;
    release.exeUrl = root + L"SummerCoop-Launcher.exe"; release.zip = release.exeUrl;
    release.checksumUrl = root + L"SummerCoop-Launcher.exe.sha256"; release.sha256 = hash; return true;
}
static bool UpdateVersionMatches(const std::wstring &path, const std::wstring &expected)
{
    std::wstring numeric = expected.substr(0, expected.find(L'-')); unsigned long long packed = 0; int count = 0; size_t start = 0;
    while (start < numeric.size() && count < 4) {
        size_t end = numeric.find(L'.', start); if (end == std::wstring::npos) end = numeric.size();
        std::wstring component = numeric.substr(start, end - start);
        if (component.empty() || component.find_first_not_of(L"0123456789") != std::wstring::npos || HeaderNumber(component) > 65535) return false;
        packed = (packed << 16) | (unsigned long long)HeaderNumber(component); count++; start = end + 1;
    }
    if (count < 3 || start < numeric.size()) return false;
    while (count++ < 4) packed <<= 16;
    return packed != 0 && ExeVersion(path) == packed;
}
