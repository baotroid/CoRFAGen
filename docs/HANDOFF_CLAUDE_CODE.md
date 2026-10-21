# Задание для Claude Code: обновить репозиторий BeGenSt до 1.4.4

Рабочая папка: `C:\Portable\AI\BeGenSt` (Windows). Репозиторий: **baotroid/BeGenSt-PDFPic2RFA** (private, ветка `main`).
Прочитай `CLAUDE.md` и `docs/HISTORY_RU.md` (строки 1.4.0–1.4.4).

1. Если в папке нет `.git`: `git init -b main`, `git remote add origin https://github.com/baotroid/BeGenSt-PDFPic2RFA.git`,
   `git fetch origin`, `git reset --soft origin/main` (история репозитория сохраняется, рабочие файлы — из папки).
   Иначе: `git pull`.
2. Проверь `.gitignore` (bin/, obj/, .vs/, Setup/, Installer/bin|obj, `~$*`) и отсутствие секретов
   (поиск `sk-ant-`, `sk-or-`, `AIza`, `Bearer ` в отслеживаемых файлах).
3. Удали `BeGenSt/Data/DeepSeekClient.cs`, если он ещё отслеживается (`git rm`); дубликат xlsx в корне — тоже.
4. `git add -A`, проверь `git status`, коммит:
   «BeGenSt 1.4.4: TypeGen (catalog types, lookup table, pipe connectors, actuator toggle), Purge full clean,
   FailureLog, skill update».
5. `git push origin main`.
6. Релиз: `gh release create v1.4.4 "Setup\BeGenSt_PDFPic2RFA_1.4.4.msi" --title "BeGenSt PDF&Pic2RFA 1.4.4" --notes-file docs/RELEASE_NOTES_1.4.4.md`
7. Проверка сборки: `dotnet build BeGenSt\BeGenSt.csproj -c "Release R24" -p:SkipDeploy=true`.
8. Сообщи ссылку на коммит и релиз.

## Синхронизация дальше
* Перед правкой: `git pull`. После правок и успешной сборки: коммит + `git push`.
* Правки, внесённые в папку Claude (Cowork), коммить с понятным сообщением.
* Новый установщик: поднять `VER` в `BuildInstaller.cmd`, собрать, `gh release create v<VER> Setup\...msi`,
  добавить строку в `docs/HISTORY_RU.md`.
