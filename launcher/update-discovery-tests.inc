// Deterministic HTTP fixtures run only through the explicit test command.
// Production URLs, redirect validation, checksum parsing and cache policy are
// exercised without changing the user's cache or querying GitHub.
static std::string g_discoveryFixture;
static int g_fixtureApiCode, g_fixtureApiCalls, g_fixtureWebCalls;
static std::wstring g_fixtureEtag;
static bool DiscoveryFixtureFetch(const std::wstring &url, std::string *body, DWORD *code, HttpReply *reply, const std::wstring &etag, bool)
{
    *reply = HttpReply{}; if (body) body->clear();
    if (url.find(L"api.github.com") != std::wstring::npos) {
        g_fixtureApiCalls++; g_fixtureEtag = etag; *code = reply->status = g_fixtureApiCode;
        reply->remaining = HeaderNumber(Widen(JsonTextMember(g_discoveryFixture, "remaining")), -1);
        reply->resetAt = HeaderNumber(Widen(JsonTextMember(g_discoveryFixture, "reset")));
        reply->retryAt = HttpRetryAt(Widen(JsonTextMember(g_discoveryFixture, "retryAfter")), (long long)_time64(NULL));
        reply->etag = L"\"fixture-etag\"";
        if (body) *body = JsonTextMember(g_discoveryFixture, "body");
    } else {
        g_fixtureWebCalls++;
        if (url.find(L"/releases/latest") != std::wstring::npos) {
            reply->location = Widen(JsonTextMember(g_discoveryFixture, "latest")); *code = reply->status = reply->location.empty() ? 404 : 302;
        } else {
            std::wstring pinned = Widen(JsonTextMember(g_discoveryFixture, "checksumUrl"));
            *code = reply->status = url == pinned ? 200 : 404;
            if (body && *code == 200) *body = JsonTextMember(g_discoveryFixture, "checksum");
        }
    }
    return *code == 200 || *code == 304;
}
static int TestUpdateDiscovery(const std::wstring &input, const std::wstring &output)
{
    std::vector<unsigned char> data; if (!ReadAll(input, data)) return 1;
    g_discoveryFixture.assign(data.begin(), data.end());
    if (JsonTextMember(g_discoveryFixture, "live") == "yes") {
        // Explicit read-only integration probe. Deterministic CI fixtures never
        // use this branch or contact GitHub.
        g_updateFetchFixture = NULL; Note pinned; bool ok = StableRelease(pinned);
        FILE *file = _wfopen(output.c_str(), L"w, ccs=UTF-8"); if (!file) return 1;
        fwprintf(file, L"live=%d http=%lu version=%s exe=%s sha=%s\n", (int)ok, g_stableCode, pinned.ver.c_str(), pinned.exeUrl.c_str(), pinned.sha256.c_str());
        fclose(file); return ok ? 0 : 1;
    }
    g_updateCacheOverride = Widen(JsonTextMember(g_discoveryFixture, "cache"));
    if (g_updateCacheOverride.empty()) return 1;
    g_updateFetchFixture = DiscoveryFixtureFetch;
    g_fixtureApiCalls = g_fixtureWebCalls = 0;
    g_fixtureApiCode = (int)HeaderNumber(Widen(JsonTextMember(g_discoveryFixture, "code")));
    bool seed = JsonTextMember(g_discoveryFixture, "seed") == "yes";
    if (seed) { int saved = g_fixtureApiCode; g_fixtureApiCode = 200; std::string first; FetchReleases(first); CacheNumber(L"Checked", 1); g_fixtureApiCode = saved; }
    std::string json; int result = FetchReleases(json);
    int before = g_fixtureApiCalls; std::string cached; int second = FetchReleases(cached);
    Note release; bool stable = StableRelease(release);
    FILE *file = _wfopen(output.c_str(), L"w, ccs=UTF-8"); if (!file) return 1;
    fwprintf(file, L"api=%d second=%d limited=%d calls=%d repeated=%d web=%d retry=%lld now=%lld\n",
        result, second, (int)g_relRateLimited, g_fixtureApiCalls, g_fixtureApiCalls - before, g_fixtureWebCalls, g_relRetryAt, (long long)_time64(NULL));
    fwprintf(file, L"etag=%s\ncache=%d\nstable=%d version=%s exe=%s sha=%s\n", g_fixtureEtag.c_str(), (int)!json.empty(), (int)stable, release.ver.c_str(), release.exeUrl.c_str(), release.sha256.c_str());
    fwprintf(file, L"matchesSelf=%d mismatchedSelf=%d\n", (int)UpdateVersionMatches(g_self, Widen(MWV_STR)), (int)UpdateVersionMatches(g_self, L"0.0.0.1"));
    fclose(file); g_updateFetchFixture = NULL; return 0;
}
