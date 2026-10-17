#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <string>
#include <vector>
#include <cwctype>
#include <cstdio>
#include "../../launcher/steam-binding.inc"

using namespace steam_binding;
static int passed;
static void Check(bool ok, const char *name) {
    if (!ok) { printf("FAIL %s\n", name); ExitProcess(1); }
    printf("PASS %s\n", name); ++passed;
}
static std::string Config(const std::string &option, const std::string &extra = "") {
    return "// Steam settings: { LaunchOptions }\r\n\"UserLocalConfigStore\"\r\n{\r\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\" {\r\n"
           "\"516750\"\r\n{\r\n\t\"cloud\" { \"last_sync_state\" \"synchronized\" }\r\n" + option + "\r\n}\r\n" + extra + "\r\n} } } }\r\n}\r\n";
}
int wmain(int argc, wchar_t **argv) {
    // Give a disposable copy a future FILEVERSION to exercise downgrade
    // protection using the real Windows version reader, without another build.
    if (argc == 3 && !_wcsicmp(argv[1], L"/future-version")) {
        HMODULE image = LoadLibraryExW(argv[2], NULL, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (!image) return 2;
        HRSRC resource = FindResourceW(image, MAKEINTRESOURCEW(1), RT_VERSION);
        DWORD size = resource ? SizeofResource(image, resource) : 0;
        const unsigned char *data = size ? (const unsigned char *)LockResource(LoadResource(image, resource)) : NULL;
        if (!data) { FreeLibrary(image); return 3; }
        std::vector<unsigned char> bytes(data, data + size);
        WORD language = 0;
        EnumResourceLanguagesW(image, RT_VERSION, MAKEINTRESOURCEW(1),
            [](HMODULE, LPCWSTR, LPCWSTR, WORD lang, LONG_PTR value) -> BOOL { *(WORD *)value = lang; return FALSE; },
            (LONG_PTR)&language);
        FreeLibrary(image);
        const unsigned char signature[] = { 0xBD, 0x04, 0xEF, 0xFE, 0, 0, 1, 0 };
        unsigned matches = 0;
        for (size_t i = 0; i + 52 < bytes.size(); ++i) if (!memcmp(bytes.data() + i, signature, sizeof(signature))) {
            DWORD future = 0x7FFF0000;
            memcpy(bytes.data() + i + 8, &future, sizeof(future));
            memcpy(bytes.data() + i + 16, &future, sizeof(future));
            ++matches;
        }
        if (matches != 1) return 4;
        HANDLE update = BeginUpdateResourceW(argv[2], FALSE);
        if (!update) return 5;
        bool ok = UpdateResourceW(update, RT_VERSION, MAKEINTRESOURCEW(1), language, bytes.data(), (DWORD)bytes.size()) != FALSE;
        ok = EndUpdateResourceW(update, !ok) && ok;
        return ok ? 0 : 5;
    }
    std::wstring current = L"D:\\SummerCoop актуальный\\SummerCoop-Launcher.exe";
    std::wstring old = L"C:\\old release\\SummerCoop-Launcher-0.4.1.exe";
    std::string flags = " -window-mode exclusive +connect_lobby 123456";
    std::string oldLine = Utf8(L"\"" + old + L"\" %command%") + flags;
    std::string other = "\"12345\" { \"LaunchOptions\" " + Quote(oldLine) + " }";
    std::string text = Config("\t\"LaunchOptions\"\t\t" + Quote(oldLine), other);
    Patch patch = Rewrite(text, current);
    std::string expected = Utf8(L"\"" + current + L"\" %command%") + flags;
    Check(patch.valid && patch.supported && patch.oldPath == old, "recognize the old Steam launcher path");
    Check(patch.text == Config("\t\"LaunchOptions\"\t\t" + Quote(expected), other), "replace only this game's value, preserving flags and all other bytes");
    Check(Rewrite(patch.text, current).text == patch.text, "registration is idempotent");
    Check(Rewrite("\xef\xbb\xbf" + text, current).text == "\xef\xbb\xbf" + patch.text, "preserve UTF-8 BOM and CRLF");
    Patch empty = Rewrite(Config("\t\"LaunchOptions\" \"\""), current);
    Check(empty.supported && empty.text.find(Quote(Utf8(L"\"" + current + L"\" %command%"))) != std::string::npos, "register an empty launch option");
    Patch missing = Rewrite(Config(""), current);
    Check(missing.supported && Rewrite(missing.text, current).text == missing.text, "insert a missing option without altering nested cloud settings");
    std::string compact = "UserLocalConfigStore { Software { Valve { Steam { apps { 516750 { } } } } } }";
    Patch inlinePatch = Rewrite(compact, current);
    Check(inlinePatch.supported && Rewrite(inlinePatch.text, current).supported, "support inline KeyValues blocks");
    std::string noPlaceholder = Config("LaunchOptions " + Quote(Utf8(L"\"" + old + L"\" -screen-fullscreen 0")));
    Check(Rewrite(noPlaceholder, current).text.find("%command%") != std::string::npos, "retain game invocation when a legacy command lacks the placeholder");
    Patch wrapper = Rewrite(Config("LaunchOptions " + Quote("\"C:\\Custom\\wrapper.exe\" %command%")), current);
    Check(wrapper.valid && !wrapper.supported && wrapper.text.empty(), "leave a custom wrapper alone");
    Check(!Rewrite(Config("LaunchOptions " + Quote("-windowed")), current).supported, "leave unrelated launch flags alone");
    Check(!Rewrite(Config("LaunchOptions " + Quote("\"SummerCoop-Launcher.exe\" %command%")), current).supported, "reject a relative executable path");
    std::string broken = text.substr(0, text.size() - 4);
    Check(!Rewrite(broken, current).valid, "reject truncated KeyValues before editing");
    Check(!Rewrite(Config("LaunchOptions \"unterminated"), current).valid, "reject an unterminated quoted string");
    Check(!Rewrite(Config("LaunchOptions \"\" LaunchOptions \"\""), current).supported, "reject ambiguous duplicate launch options");
    Check(!Rewrite(Config("LaunchOptions { value \"\" }"), current).supported, "reject an object instead of a launch command");
    std::string wrongBranch = "UserLocalConfigStore { Fake { Software { Valve { Steam { apps { 516750 { LaunchOptions \"\" } } } } } } }";
    Check(!Rewrite(wrongBranch, current).supported, "do not match an app ID in another subtree");
    std::string duplicateApp = "UserLocalConfigStore { Software { Valve { Steam { apps { 516750 { } 516750 { } } } } } }";
    Check(!Rewrite(duplicateApp, current).supported, "reject duplicate My Summer Car blocks");
    std::string caseFold = "userlocalconfigstore { software { valve { steam { Apps { 516750 { launchoptions \"\" } } } } } }";
    Check(Rewrite(caseFold, current).supported, "match Steam's keys regardless of casing");
    Check(LauncherName(L"C:\\old\\MWCoop.exe") && LauncherName(L"C:\\old\\MWCoop-Launcher.exe"), "recognize legacy MWCoop launcher names");
    Check(!LauncherName(L"C:\\old\\SummerCoop-Launcher-malware.exe") && !LauncherName(L"C:\\old\\mysummercar.exe"), "only recognize known executable names");
    std::string line; std::wstring path;
    Check(LaunchLine("C:/old/MWCoop.exe %command%", current, line, path) && path == L"C:\\old\\MWCoop.exe", "normalize absolute paths from old commands");
    Check(!Rewrite(Config("LaunchOptions " + Quote(std::string("\xff", 1))), current).supported, "reject invalid UTF-8 commands");
    printf("ALL %d STEAM BINDING PARSER TESTS PASSED\n", passed);
    return 0;
}
