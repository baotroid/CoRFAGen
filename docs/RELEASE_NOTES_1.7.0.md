# CoRFAGen 1.7.0: plug-in for Autodesk Revit

Установщик для Autodesk Revit 2024–2027 заменяет прежнюю версию. Перед установкой закройте Revit.

## Что нового
* **Русский язык интерфейса.** CoRFAGen › Settings › «Interface language»: выберите «Русский». Окно, лента и 3D-просмотр переключаются сразу, выбор сохраняется.
* Переведены окна, подсказки, диалоги, сообщения об ошибках и 3D-просмотр. Ответы ИИ, которые читает пользователь (вопросы о противоречиях, замечания проверки), тоже приходят на русском.
* Не переводятся название плагина и подписи кнопок, кроме кнопки Settings на ленте: она становится «Настройки». Панель на ленте называется «Генератор семейств».
* Файлы отчётов и журналы остаются на английском. Сообщения, которые приходят от сервисов ИИ и не описаны в словаре, показываются как есть.

## Исправления
* В заголовках окон логотип теперь CoRFAGen, а не RFA|Gen.
* На флажке таблицы выбора снова видно `size_lookup`: подчёркивание пропадало.

## Как проверено
* Сборка для Revit 2024–2027 без ошибок.
* Все окна на русском и английском открыты и отрисованы вне Revit, на сборке для Revit 2024. Перевод 3D-просмотра проверен в WebView2 на реальной модели.
* **В самом Revit эта версия ещё не запускалась:** смена языка на ленте, диалоги Revit и работа с ИИ не проверены. Поэтому это предварительный выпуск.

---

# CoRFAGen 1.7.0: plug-in for Autodesk Revit (English)

The installer for Autodesk Revit 2024–2027 replaces the earlier version. Close Revit before installing.

## What's new
* **Russian interface.** CoRFAGen › Settings › "Interface language": choose "Русский". The window, the ribbon and the 3D viewer switch at once, and the choice is saved.
* Windows, tooltips, dialogs, error messages and the 3D viewer are translated. AI answers the user reads (conflict questions, check remarks) also come in Russian.
* The plug-in name and button labels are not translated, except the Settings ribbon button, which becomes «Настройки». The ribbon panel is called «Генератор семейств».
* Report files and logs stay in English. Messages from AI services that are not in the dictionary are shown as they are.

## Fixes
* The logo in window headers now reads CoRFAGen, not RFA|Gen.
* The lookup table check box shows `size_lookup` again: the underscore was lost.

## How it was checked
* The build for Revit 2024–2027 has no errors.
* All windows were opened and rendered in Russian and English outside Revit, on the Revit 2024 build. The 3D viewer translation was checked in WebView2 on a real model.
* **This version has not run inside Revit yet:** the ribbon language switch, Revit dialogs and work with the AI are untested. This is why it is a pre-release.

Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc., and/or its subsidiaries and/or affiliates in the USA and/or other countries. CoRFAGen is an independent product and is not affiliated with, endorsed or sponsored by Autodesk, Inc.
