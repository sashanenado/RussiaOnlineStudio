# Russia Online Studio

Настольная мастерская для правовой базы и игровых памяток проекта **Russia Online (Arbat)**. Приложение состоит из двух отдельных Windows-программ, которые работают с общей папкой данных.

## Что входит в приложение

| Программа | Назначение |
|---|---|
| `Studio.exe` | Библиотека законов, редактор статей, профили, конструктор памяток, форум во встроенном браузере и настройки |
| `Overlay.exe` | Прозрачные независимые окна памяток поверх игры, управление окнами и горячие клавиши |

Сохранены три независимых профиля: **Основной**, **Заместитель** и **Личный**. Изменения в базе Studio передаются в Overlay автоматически; вручную копировать статьи не требуется.

## Быстрый запуск

1. Скачай ZIP-архив сборки Windows и распакуй **всю папку целиком** в место, где у текущего пользователя есть право записи (например, в «Документы»).
2. Запусти `Studio.exe`. Выбери профиль и работай с библиотекой или конструктором панелей.
3. Запусти `Overlay.exe` из той же папки. Окна памяток создаются автоматически по настройкам активного профиля.
4. Для встроенной вкладки форума нужен **Microsoft Edge WebView2 Runtime**. Вход на форум хранится в пользовательском профиле Windows, отдельно от переносимой папки Data.

Не перемещай EXE-файлы отдельно от папки `Data`: обе программы используют общую структуру рядом с собой.

## Данные и резервное копирование

Папка `Data` содержит исходную базу, расширенный каталог Arbat, профили, настройки и координаты Overlay. Перед обновлением программы:

- закрой Studio и Overlay;
- сделай копию всей папки `Data`;
- распакуй новые EXE в ту же папку или в новую папку, затем перенеси свою резервную `Data`.

Не удаляй `Data/Profiles` и `Data/overlay_positions.json`, если хочешь сохранить пользовательские изменения и расположение окон. Файлы ошибок Studio находятся в `Data/logs/errors.log`.

## Горячие клавиши Overlay

| Клавиша | Действие |
|---|---|
| F8 | Выход из Overlay |
| F9 | Показать или скрыть все окна памяток |
| F10 | Показать панель управления Overlay |
| F11 | Переключить режим перемещения окон |

## Сборка для разработчика

- Решение состоит из двух WPF-проектов на **.NET 10 for Windows**.
- `src/Studio/Studio.csproj` собирает Studio и использует Microsoft WebView2.
- `src/Overlay/Overlay.csproj` собирает Overlay.
- GitHub Actions проверяет JSON-каталог и публикует оба приложения в portable ZIP для Windows x64.
- Рабочая ветка разработки: `development`. Основная ветка: `main`.

Локальная сборка на Windows:

```powershell
dotnet publish src/Studio/Studio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/RussiaOnline/studio_tmp

dotnet publish src/Overlay/Overlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/RussiaOnline/overlay_tmp
```

Публикация self-contained включает .NET runtime в приложения; WebView2 Runtime для вкладки форума устанавливается отдельно. Успешная сборка не заменяет проверку реального запуска, сохранения данных, горячих клавиш и работы Overlay поверх игры.
