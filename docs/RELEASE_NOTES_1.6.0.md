# CoRFAGen 1.6.0: plug-in for Autodesk Revit

Прежнее название: BeGenSt PDF&Pic2RFA. Установщик для Autodesk Revit 2024–2027 заменяет прежнюю версию. Перед установкой закройте Revit.

## Что изменилось
* Новое название: вкладка **CoRFAGen**, панель **Family Generator**, кнопка **CoRFAGen** в окне предпросмотра.
* Настройки и API-ключи переносятся автоматически при первом запуске: копируются из `%AppData%\BeGenSt\RFAGen` в `%AppData%\CoRFAGen`.
  Старая папка остаётся, в ней по-прежнему лежат ключи; её можно удалить вручную. Новые журналы пишутся в `%AppData%\CoRFAGen\Logs`.
* Семейства, созданные прежними версиями, работают как раньше.
* Навык теперь называется `corfagen-json-from-drawing`.
* При первом запуске Revit спросит о надстройке без цифровой подписи (плагин не подписан, имя новое). Выберите «Всегда загружать» или «Загрузить один раз».

## Как проверено
* Установка заменяет прежнюю версию: проверено обновление с 1.4.4, старые файлы удалены для всех версий Revit.
* В Revit 2024: плагин загружается, кнопки создаются, окна, чтение DWG и генерация семейства работают.
* Кнопки ленты вручную не нажимались, запуск через AI с API-ключом и работа в Revit 2025–2027 не проверялись.

Autodesk и Revit являются зарегистрированными товарными знаками или товарными знаками компании Autodesk, Inc., и/или её дочерних и аффилированных компаний в США и/или других странах. CoRFAGen — независимый продукт, не связанный с компанией Autodesk, Inc. и не одобренный ею.

---

# CoRFAGen 1.6.0: plug-in for Autodesk Revit (English)

Formerly BeGenSt PDF&Pic2RFA. The installer for Autodesk Revit 2024–2027 replaces the earlier version. Close Revit before installing.

## What changed
* New name: tab **CoRFAGen**, panel **Family Generator**, **CoRFAGen** button in the preview window.
* Settings and API keys move automatically on first run: they are copied from `%AppData%\BeGenSt\RFAGen` to `%AppData%\CoRFAGen`.
  The old folder stays and still holds the keys; you can delete it by hand. New logs go to `%AppData%\CoRFAGen\Logs`.
* Families made with earlier versions keep working as before.
* The skill is now called `corfagen-json-from-drawing`.
* On first start Revit asks about an add-in without a digital signature (the plug-in is unsigned and the name is new). Choose "Always Load" or "Load Once".

## How it was checked
* Installing replaces the earlier version: the upgrade from 1.4.4 was checked, and the old files are gone for all Revit versions.
* In Revit 2024: the plug-in loads, the buttons are created, and the windows, DWG reading and family generation work.
* The ribbon buttons were not clicked by hand; the path through the AI with an API key and Revit 2025–2027 were not tested.

Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc., and/or its subsidiaries and/or affiliates in the USA and/or other countries. CoRFAGen is an independent product and is not affiliated with, endorsed or sponsored by Autodesk, Inc.
