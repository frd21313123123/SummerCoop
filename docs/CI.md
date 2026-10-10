# GitHub Actions

Workflow [build.yml](../.github/workflows/build.yml) запускается после каждого push, для pull request и вручную через Actions → Build SummerCoop → Run workflow.

На стандартной виртуальной машине GitHub `windows-latest` компилируется ядро с C# 5 и выполняются 46 проверок, включая настоящие TCP/UDP-сокеты. Игра и её DLL этому этапу не нужны. Отчёт доступен в журнале шага и сводке запуска. Загрузка артефактов не используется: квота хранения аккаунта была заполнена, а для проверки ядра отдельный ZIP не нужен.

Локальный runner удалён по просьбе владельца. Регистрации в GitHub, фонового процесса и автозапуска на компьютере нет.

## Полная сборка

Полная сборка мода требует Mono, Unity, PlayMaker, ES2, Steamworks и MSCLoader из установленной игры. Эти файлы не включены в репозиторий или артефакты CI. На стандартной машине GitHub их нет, поэтому текущий workflow не создаёт установочный ZIP и не проверяет компиляцию игрового адаптера.

Соберите мод на компьютере с установленной игрой и MSCLoader:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1 -GamePath "E:\SteamLibrary\steamapps\common\My Summer Car"
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package.ps1
```

Архивы появятся в `dist`. Сборка не устанавливает мод в игру и не меняет сохранения. Инструкция установки находится в [README.md](../README.md), игровые проверки — в [QA.md](QA.md).

Действие `checkout` закреплено на SHA официального выпуска. Разрешения workflow ограничены `contents: read`.
