// Runs inside the actual native EXE before game discovery or Steam registration.
// Every path, including LocalLow and launcher settings, is redirected to a fixture.
static int TestNativeSaves(const std::wstring &root, const std::wstring &reportPath)
{
    FILE *report = _wfopen(reportPath.c_str(), L"wb");
    if (!report) return 1;
    auto check = [&](bool ok, const char *name) { fprintf(report, "%s %s\r\n", ok ? "PASS" : "FAIL", name); return ok; };
    auto write = [&](const std::wstring &path, const char *text) {
        SHCreateDirectoryExW(NULL, DirOf(path).c_str(), NULL);
        FILE *file = _wfopen(path.c_str(), L"wb");
        if (!file) return false;
        fputs(text, file); fclose(file); return true;
    };
    auto run = [&]() -> bool {
        SetEnvironmentVariableW(L"USERPROFILE", (root + L"\\user").c_str());
        SetEnvironmentVariableW(L"LOCALAPPDATA", (root + L"\\local").c_str());
        g_gameDir = WithSlash(root + L"\\game");
        SHCreateDirectoryExW(NULL, g_gameDir.c_str(), NULL);
        g_iniLauncher = root + L"\\summercoop-launcher.ini";
        if (!write(g_iniLauncher, "[Lanceur]\r\nLangue=ru\r\n")) return false;
        g_lobby = LB_HOST; g_lobbySteam = false; g_goSent = false;
        if (!write(SaveDirOf(L"") + L"defaultES2File.txt", "previous host world")) return false;
        for (int i = 0; i < 8; i++) LobbySetPartie(PARTIE_NOUVELLE);
        std::wstring newest = SaveCurrentProf();
        if (!check(g_saves.size() == 9 && newest == L"partie-8" && g_partie == PARTIE_NOUVELLE && g_saveModal == SM_NONE,
                   "New game immediately creates and selects a separate world in the current lobby, including repeated clicks")) return false;
        for (const SaveSlot &slot : g_saves) if (!slot.prof.empty() && !check(!slot.has, "New world contains no old game data")) return false;
        write(SaveDirOf(L"partie-1") + L"defaultES2File.txt", "first world");
        SavesScan();
        if (!check(g_saves[0].has && g_saves[1].has && !g_saves[8].has, "Save list detects MSC native save files")) return false;
        UiClick(UI_SAVE + 8 + SP_USE);
        if (!check(SaveCurrentProf() == L"partie-1" && g_partie == PARTIE_CONTINUER, "Use switches the lobby from new game to continuing the selected old world")) return false;
        SaveAskName(false); g_saveNameText = L"С друзьями"; UiClick(UI_SAVE_MOK);
        if (!check(SaveCurrentProf() == L"partie-9" && SaveCurrentName() == L"С друзьями" && g_partie == PARTIE_NOUVELLE,
                   "Creating a named save from the picker updates the active lobby")) return false;
        SaveOpenPick();
        Bitmap bitmap(1280, 800, PixelFormat32bppARGB); Graphics graphics(&bitmap);
        g_zones.clear(); DrawSaveModal(graphics);
        auto hasZone = [&](int id) { for (const HotRect &zone : g_zones) if (zone.id == id) return true; return false; };
        if (!check(hasZone(UI_SAVE_PICK + 9), "Picker opens on the page containing the selected world")) return false;
        UiClick(UI_SAVE_PREV); g_zones.clear(); DrawSaveModal(graphics);
        if (!check(hasZone(UI_SAVE_MNEW) && hasZone(UI_SAVE_NEXT), "Picker retains New save and page controls when many worlds exist")) return false;
        CLSID png;
        if (EncoderClsid(L"image/png", &png)) bitmap.Save((root + L"\\picker.png").c_str(), &png, NULL);
        UiClick(UI_SAVE_NEXT); g_zones.clear(); DrawSaveModal(graphics);
        if (!check(hasZone(UI_SAVE_PICK + 9) && hasZone(UI_SAVE_MNEW), "Later saves remain clickable on the next picker page")) return false;
        if (EncoderClsid(L"image/png", &png)) bitmap.Save((root + L"\\picker-page-2.png").c_str(), &png, NULL);
        UiClick(UI_SAVE_PICK + 9);
        if (!check(SaveCurrentProf() == L"partie-9" && g_partie == PARTIE_NOUVELLE, "Choosing an empty save starts a new world")) return false;
        SaveOpenPick(); UiClick(UI_SAVE_PICK + 1);
        if (!check(SaveCurrentProf() == L"partie-1" && g_partie == PARTIE_CONTINUER, "Picker restores the selected saved world")) return false;
        if (!WriteLaunchFile(MODE_HOST, L"", 27842, PartieName(g_partie))) return false;
        std::vector<unsigned char> bytes; ReadAll(g_gameDir + L"MWCoop\\lancement.ini", bytes);
        std::string launch(bytes.begin(), bytes.end());
        if (!check(launch.find("Profil=partie-1\r\n") != std::string::npos && launch.find("Partie=continuer\r\n") != std::string::npos
                   && launch.find("ProfilesRoot=" + Narrow(SaveProfRoot(), CP_UTF8) + "\r\n") != std::string::npos,
                   "Launch request passes the chosen world and original profiles directory to the mod")) return false;
        g_lobby = LB_NONE; g_partie = PARTIE_NOUVELLE; g_goSent = true;
        if (!check(SaveCurrentProf() == L"partie-1", "Selected save survives a new lobby and settings reload")) return false;
        if (!check(SaveSetCurrent(L"partie-2") && SaveSetCurrent(L"partie-1"), "Saves remain selectable after a previous lobby has launched and closed")) return false;
        g_lobby = LB_GUEST;
        if (!check(!SaveSetCurrent(L"partie-2") && SaveCurrentProf() == L"partie-1", "Guests cannot change their own selected host world")) return false;
        ReadAll(SaveDirOf(L"") + L"defaultES2File.txt", bytes);
        if (!check(std::string(bytes.begin(), bytes.end()) == "previous host world", "New lobby worlds preserve the previous main world")) return false;
        fprintf(report, "ALL NATIVE SAVE TESTS PASSED\r\n");
        return true;
    };
    bool ok = run(); fclose(report); return ok ? 0 : 1;
}
