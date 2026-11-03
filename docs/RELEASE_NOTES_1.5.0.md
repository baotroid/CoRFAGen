# BeGenSt PDF&Pic2RFA 1.5.0

Установщик для Revit 2024–2027 (заменяет предыдущие версии). Перед установкой закройте Revit.

## DWG и DXF как исходные файлы
* Generate и TypeGen принимают DWG и DXF. AutoCAD и преобразование DWG в DXF не нужны.
* Работает с Claude, Gemini и Qwen: модель получает изображение листа и точные данные текстом.
* Числа берутся из файла, а не считываются с картинки: подпись и длина по геометрии каждого размера, тексты и таблицы,
  окружности, координаты линий.
* Лист находится автоматически, объекты вне рамки отбрасываются, блоки разворачиваются, тексты декодируются.
* Виды в своём масштабе определяются автоматически, подписи, расходящиеся с геометрией вида, отмечаются для проверки исходных данных.
* Данные, переданные модели, сохраняются в папке Logs (`…-cad-data.txt`).

## Ограничения
* Читается только 2D: 3D-тела из DWG и внешние ссылки не читаются.
* Нестандартные объекты других программ (например, `CYKYDimEntity`) могут быть пропущены, модель получает их количество.

## Как проверено
* 4 чертежа DWG разных авторов читаются в процессе Revit 2024: размеры, тексты и изображение листа в порядке.
* Семейство из JSON, собранного по данным DWG, построено в Revit 2024: все элементы, без ошибок, габариты совпали.
* **Полный путь через AI (с API-ключом) ещё не проверялся.** Revit 2025–2027 проверены только сборкой и чтением на .NET 8.

## Навык rfagen-json-from-drawing
Семейство из DWG без API-ключа: инструмент `dwg_extract`, правила чтения DWG, проверка генерации в Revit на новом документе семейства.

---

# BeGenSt PDF&Pic2RFA 1.5.0 (English)

Installer for Revit 2024–2027 (replaces earlier versions). Close Revit before installing.

## DWG and DXF as source files
* Generate and TypeGen accept DWG and DXF. Neither AutoCAD nor a DWG to DXF conversion is needed.
* Works with Claude, Gemini and Qwen: the model gets an image of the sheet and exact data as text.
* Numbers come from the file, not from reading a picture: the label and drawn length of every dimension, texts and tables,
  circles, line coordinates.
* The sheet is found automatically, objects outside the frame are dropped, blocks are expanded, texts are decoded.
* Views at their own scale are detected automatically; labels that differ from the geometry of their view are flagged for the source data check.
* The data sent to the model is saved in the Logs folder (`…-cad-data.txt`).

## Limitations
* 2D only: 3D solids in a DWG and external references are not read.
* Custom objects of other programs (e.g. `CYKYDimEntity`) may be skipped; the model is told how many.

## How it was checked
* 4 DWG drawings from different authors are read inside the Revit 2024 process: dimensions, texts and the sheet image are correct.
* A family from a JSON built from DWG data was generated in Revit 2024: all elements, no errors, overall size as expected.
* **The full path through the AI (with an API key) has not been tested yet.** Revit 2025–2027 were checked by build and by reading on .NET 8 only.

## Skill rfagen-json-from-drawing
Family from a DWG without an API key: the `dwg_extract` tool, DWG reading rules, generation check in Revit on a new family document.
