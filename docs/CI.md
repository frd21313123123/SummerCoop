# GitHub Actions

Workflow [build.yml](../.github/workflows/build.yml) запускается после каждого
push в `main`, для pull request и вручную. Все сборки выполняются на
стандартной машине GitHub `windows-latest`; локальный runner не используется.

Проверки ядра, транспорта, установки, интерфейса и самообновления работают
без игры и без Secrets. Полная сборка и публикация доступны только из `main`.
Она расшифровывает закрытый архив зависимостей игры, заново компилирует
`SummerCoop.dll` и `SummerCoop.Core.dll`, проверяет адаптер MSC и собирает
статически скомпонованный нативный лаунчер со встроенными DLL и изображениями.
Готовый файл находится в `SummerCoop-Launcher/SummerCoop-Launcher.exe`;
резервная копия сохраняется в `dist/SummerCoop-Launcher.exe`.

Версия выпуска: `<версия мода>.<GITHUB_RUN_NUMBER>`, например `0.4.8.53`.
Каждый push получает отдельный тег, EXE и архивы. Выпуск сначала создаётся
как черновик и становится виден лаунчерам после загрузки и проверки всех
файлов. Публикуются обычный и версионный EXE, их `.sha256`, архив мода и
архив исходников. Лимит хранилища Actions artifacts не используется.
Подробнее о поведении клиента: [UPDATES.md](UPDATES.md).

## Закрытые зависимости для компиляции

`ci/msc-references.zip.aes` содержит только десять управляемых DLL,
необходимых компилятору: Mono BCL, UnityEngine, PlayMaker, ES2, MSCLoader,
Harmony и игровые сборки. Архив зашифрован AES-256-CBC и защищён HMAC-SHA256; незашифрованные библиотеки
не включаются в Git, исходный ZIP или публичные release assets. Ключ хранится
только в repository secret `SUMMERCOOP_REFS_PASSPHRASE`. Это схема хранения
больших секретов, описанная в
[документации GitHub](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets#storing-large-secrets).

Для первоначальной настройки или обновления зависимостей задайте
`SUMMERCOOP_REFS_PASSPHRASE` в локальном окружении безопасным способом и выполните:

```powershell
./tools/ci-references.ps1 -Mode Pack -GamePath 'E:\SteamLibrary\steamapps\common\My Summer Car'
```

Сохраните тот же ключ в Settings → Secrets and variables → Actions →
`SUMMERCOOP_REFS_PASSPHRASE` либо передайте его через stdin в
`gh secret set SUMMERCOOP_REFS_PASSPHRASE --repo frd21313123123/SummerCoop`.
Публиковать нужно только `.aes`. После настройки обычный push не требует
ручной сборки или повторной передачи ключа. При смене ключа замените архив
и Secret согласованно.

Шифрование использует встроенные библиотеки .NET, PBKDF2 с 200 000 итерациями, случайные salt и IV. Скрипт создаёт
временный ZIP в `build/` и удаляет его при завершении. После расшифровки
проверяется HMAC всего архива, затем SHA-256 каждой DLL. В CI зависимости удаляются после завершения
job, включая случай неудачной сборки. Приватные зависимости не передаются
в pull request jobs.

## Полная сборка локально

```powershell
./tools/package.ps1 -RebuildMod -GamePath 'E:\SteamLibrary\steamapps\common\My Summer Car'
```

Для воспроизведения сборки с номером CI можно передать `-Version 0.4.8.53`.
Скрипт `tools/release-version.ps1 -BuildNumber 53` вычисляет такую версию по
исходникам. Локальная сборка не устанавливает мод в игру.

Права workflow ограничены `contents: read`; только job публикации получает
`contents: write`. Токен публикации передаётся только шагу загрузки выпуска,
а ключ зависимостей — только шагу расшифровки. `checkout` закреплён на SHA
официального выпуска.
