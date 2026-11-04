# CoRFAGen 1.7.2: plug-in for Autodesk Revit

Установщик для Autodesk Revit 2024–2027 заменяет прежнюю версию. Перед установкой закройте Revit.

## Что нового по сравнению с 1.6.0
* **Русский язык интерфейса.** CoRFAGen › Settings › «Interface language»: выберите «Русский». Окно, лента и 3D-просмотр переключаются сразу, выбор сохраняется.
* Переведены окна, подписи кнопок, подсказки, диалоги, сообщения об ошибках и 3D-просмотр. Кнопки на ленте: Очистка, Генерация, Типоразмеры, Настройки. Название CoRFAGen не переводится.
* Ответы ИИ, которые читает пользователь (вопросы о противоречиях, замечания проверки), тоже запрашиваются на русском.
* **Новые значки на ленте:** оттенки синего с оранжевым элементом на каждой кнопке.
* **Настройки:** первой строкой идёт язык интерфейса, под ней настройки ИИ.
* Файлы отчётов и журналы остаются на английском. Сообщения от сервисов ИИ, которых нет в словаре, показываются как есть.

## Исправления
* В заголовках окон логотип теперь CoRFAGen, а не RFA|Gen.
* На флажке таблицы выбора снова видно `size_lookup`: подчёркивание пропадало.
* В сборке 1.7.1, которая не публиковалась, для Revit 2024 оставались старые значки. Теперь установщик собирает плагин с нуля.

## Как проверено
* Сборка для Revit 2024–2027 без ошибок.
* Значки сверены внутри DLL из установщика для всех четырёх версий Revit.
* Установщик поставлен и проверен в Revit: значки на ленте и окно настроек.
* Все окна отрисованы на русском и английском вне Revit, на сборке для Revit 2024. Перевод 3D-просмотра проверен в WebView2 на реальной модели.
* **Отдельно не проверялись:** работа с ИИ на русском языке и запуск в Revit 2025–2027.

---

# CoRFAGen 1.7.2: plug-in for Autodesk Revit (English)

The installer for Autodesk Revit 2024–2027 replaces the earlier version. Close Revit before installing.

## What's new since 1.6.0
* **Russian interface.** CoRFAGen › Settings › "Interface language": choose "Русский". The window, the ribbon and the 3D viewer switch at once, and the choice is saved.
* Windows, button labels, tooltips, dialogs, error messages and the 3D viewer are translated. Ribbon buttons: Очистка, Генерация, Типоразмеры, Настройки. The name CoRFAGen is not translated.
* AI answers the user reads (conflict questions, check remarks) are also requested in Russian.
* **New ribbon icons:** shades of blue with an orange element on each button.
* **Settings:** the interface language comes first, the AI settings below it.
* Report files and logs stay in English. Messages from AI services that are not in the dictionary are shown as they are.

## Fixes
* The logo in window headers now reads CoRFAGen, not RFA|Gen.
* The lookup table check box shows `size_lookup` again: the underscore was lost.
* The unpublished 1.7.1 build for Revit 2024 kept the old icons. The installer now builds the plug-in from scratch.

## How it was checked
* The build for Revit 2024–2027 has no errors.
* The icons were compared inside the DLLs from the installer, for all four Revit versions.
* The installer was installed and checked in Revit: ribbon icons and the Settings window.
* All windows were rendered in Russian and English outside Revit, on the Revit 2024 build. The 3D viewer translation was checked in WebView2 on a real model.
* **Not checked separately:** work with the AI in Russian, and running in Revit 2025–2027.

Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc., and/or its subsidiaries and/or affiliates in the USA and/or other countries. CoRFAGen is an independent product and is not affiliated with, endorsed or sponsored by Autodesk, Inc.
