# Правила проекта SummerCoop для моделей Gemini / Antigravity

См. полный регламент в [AGENTS.md](AGENTS.md).

## Ключевое требование к лаунчеру:
1. Выходная папка для готового лаунчера: `SummerCoop-Launcher/` в корне репозитория.
2. Целевой файл: `SummerCoop-Launcher/SummerCoop-Launcher.exe`.
3. В этой папке должен быть **только** этот `.exe` файл (и `.sha256` / рабочий `summercoop-launcher.ini`). Никаких дополнительных внешних DLL или ассетов класть туда нельзя, так как лаунчер полностью автономен (все ресурсы и DLL мода зашиты внутри).
4. `tools/build-launcher.ps1` и `tools/package.ps1` обязаны поддерживать сборку и обновление исполняемого файла в `SummerCoop-Launcher/SummerCoop-Launcher.exe`.
