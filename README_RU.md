# BeGenSt · PDF&Pic2RFA · RFAGen

*Русский · [English version below](#english-version)*

Надстройка Revit: семейство RFA из PDF-чертежа, фото и эскиза с помощью ИИ (Anthropic Claude, Google Gemini или Alibaba Qwen).
Основана на открытом проекте ArchSmarter FamFab (MIT, см. LICENSE) и существенно переработана.

## Интерфейс в Revit

Вкладка **BeGenSt** → панель **PDF&Pic2RFA**:

* **Purge** (первая кнопка) — очистка семейства, см. ниже.
* **Generate** — выбор исходных файлов, анализ, генерация, проверка, 3D-предпросмотр.
* **TypeGen** — семейство с несколькими типоразмерами по таблице каталога (см. ниже).
* **Settings** — провайдер ИИ (Claude / Gemini / Qwen), API-ключи, модель.

В окне предпросмотра кнопка **RFAGen** создаёт семейство в редакторе семейств, **Refine** — уточняет модель текстом.

## Исходные данные

До **3 файлов одного объекта**: DWG/DXF или PDF (векторный/сканированный чертёж с ≥ 2 видами, техданные), JPG/JPEG (фото),
PNG (эскиз от руки). Приоритет при расхождениях: размеры на DWG/DXF и PDF > размеры на эскизе > пропорции чертежа > эскиз > фото.

## DWG и DXF

Чертёж читается внутри плагина библиотекой ACadSharp (MIT), AutoCAD и преобразование DWG в DXF не нужны. Ни одному провайдеру
файл DWG не передаётся: модель получает изображение листа и точные данные текстом, так что это работает с Claude, Gemini и Qwen.

* Находится лист (рамка), объекты вне него отбрасываются; блоки разворачиваются; тексты декодируются (в том числе `\U+XXXX`).
* Для каждого размера передаются подпись и длина по геометрии. Подпись считается проектным значением, виды в своём масштабе
  определяются автоматически, а расхождения внутри вида отмечаются для проверки исходных данных.
* Данные для модели сохраняются в папке Logs (`…-cad-data.txt`).
* Читается только 2D. 3D-тела из DWG и внешние ссылки не читаются, нестандартные объекты других программ могут быть пропущены
  (их число указывается для модели).

## Qwen (Alibaba Cloud Model Studio)

С версии 1.2 вместо DeepSeek (DeepSeek ужимал каждое изображение до ~1300×1300 px, и мелкая графика чертежа
терялась). Ключ создаётся в Model Studio → API Key (вид `sk-…`) и вводится в **Settings** → *Alibaba Qwen API Key*.
Ключи привязаны к региону: поле *Qwen endpoint* должно соответствовать региону ключа
(Сингапур — `https://dashscope-intl.aliyuncs.com/compatible-mode/v1` или
`https://<WorkspaceId>.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1`).

API Qwen не принимает PDF, поэтому надстройка **растеризует PDF локально** (pdfium, Docnet.Core): первые 3 страницы
в высоком разрешении (длинная сторона 3600 px, ≈9 Мп; Qwen читает до 16 Мп на изображение), плюс текстовый слой
векторного PDF (размеры, штамп, техданные). Модель по умолчанию `qwen3.7-plus` (режим рассуждений включён,
бюджет 16k токенов), в списке также `qwen3.8-max`, `qwen3.7-flash`, `qwen3.6-plus`, `qwen3-vl-plus`.

**Через OpenRouter (например, бесплатная `qwen/qwen3.8-27b:free`).** Модели с «/» в имени отправляются в OpenRouter
(`https://openrouter.ai/api/v1`) с ключом из поля *OpenRouter API Key* (`sk-or-…`); поле Qwen endpoint для них не
используется. У бесплатных моделей OpenRouter есть лимиты запросов в минуту и в день — одна генерация = 2–3 запроса.

## Purge — два режима

* **Purge unused elements** — как «Удалить неиспользуемые» Revit (несколько проходов) + сохранение со сжатием.
* **Full clean before generation** — подготовка существующего семейства к новой генерации: удаляются геометрия
  (тела и пустоты), коннекторы, вложенные семейства, размеры, линии, опорные линии, собственные опорные плоскости
  (плоскости шаблона остаются), таблицы выбора и формулы `size_lookup`; затем — Purge. Параметры семейства сохраняются.
  TypeGen сам предлагает этот режим, если в семействе есть такое содержимое.

## TypeGen — семейство с типоразмерами по каталогу

Для каталожных листов с таблицей типоразмеров (пример — клапаны КЗРр/КРр ГК «Авангард», DN15–300, PN16–40):

1. **Add files…** — лист каталога PDF (таблица + чертёж), до 3 файлов.
2. **1 · Read type table** — ИИ извлекает таблицу «Габаритные и присоединительные размеры» (объединённые ячейки PN
   разворачиваются в отдельные строки), стандартный ЭИМ и ΔP по DN, таблицу фигур, примечания. Таблица редактируется,
   строки можно снимать галочкой.
3. **Figure / Prefix / Type name pattern** — выбор фигуры (25с947нж …) отсекает строки с недопустимым для материала PN;
   имена типов по шаблону `{Префикс} {Обозначение} DN{DN} PN{PN}` → «КЗРр 25с947нж DN50 PN16».
4. **2 · Generate model** — ИИ строит ОДИН параметрический шаблон (размеры — выражения от D1, D2, D3, L, n, d, H, DN),
   плагин проверяет каждый тип (длина = L, высота = H); **3D preview** — любой тип.
5. **RFAGen** — создаёт семейство: тип на каждую строку, параметры типа из таблицы, «Описание» по типу,
   геометрия наборами с видимостью (одинаковые размеры — общий набор), отверстия фланцев массивом по n,
   привод скрывается галочкой экземпляра **«Показать_привод»**. После создания — автоматический Purge и
   предложение сохранить со сжатием.
   * **Pipe connectors (DN) · Pipe Accessories** — два трубных коннектора (система Global) на торцах ±L/2, диаметр = DN
     выбранного типа; семейство «Обобщённые модели» переводится в категорию «Арматура трубопроводов»
     (тип детали «Клапан — врезка»).
   * **Lookup table (size_lookup by DN, PN)** — таблица встраивается в семейство как таблица выбора **BGS_Catalog**
     (Управление → Таблицы выбора); числовые параметры типа (D1, D2, D3, L, d, H, n …) получают формулы
     `size_lookup(BGS_Catalog, "Vi", …, DN, PN)`. Типы по-прежнему создаются для каждой строки. Если формула не
     воспроизводит таблицу, параметр остаётся с обычными значениями по типам (см. отчёт).

**Save / Load catalog JSON** — каталог (шаблон + таблица) сохраняется и загружается без API-ключа.
Готовый пример: `TestDrawings/Avangard_KZRr_25s947nzh_catalog.json` (40 типов, 23 набора геометрии, 27 элементов шаблона:
фланцы с отверстиями, конические патрубки, корпус-«глобус», крышка со шпильками, бугель, шток, привод с двигателем и маховиком).

## Параметры семейства и ФОП

В окне Generate:

* **Fill the parameters of this family / template** — техданные и габариты записываются в уже существующие
  параметры открытого семейства/шаблона (те же имена; ничего не дублируется, существующие параметры не удаляются).
  Язык семейства определяется автоматически (по именам параметров, опорных плоскостей, видов, уровней):
  если оно русское — новые параметры и текстовые значения создаются на русском, иначе на английском.
* **Use parameters from a shared parameter file (ФОП)** — выберите .txt файл общих параметров; совпавшие по смыслу
  данные добавляются в семейство как **общие** параметры из этого файла (с их GUID) и заполняются с пересчётом единиц.
  Настройка ФОП в самом Revit после генерации возвращается к прежней.
* Данные, для которых не нашлось параметра, создаются как обычные параметры семейства на языке семейства.
* Сопоставление и перевод выполняются и для JSON, загруженного кнопкой **Load JSON…**: при наличии API-ключа —
  выбранной моделью ИИ (короткий текстовый запрос), без ключа — встроенным словарём терминов. Переименованные
  параметры автоматически заменяются и в выражениях геометрии.
* **Тип семейства** получает имя, указанное в плагине (Family name).
* **Описание** заполняется автоматически: «Наименование» + технические характеристики из исходных файлов
  (например, «Циклон ЦН-15. Масса: 120 кг; Производительность: 2500 м3/ч»).

## Purge — размер файла

Большой размер сгенерированных семейств (5–9 МБ) обычно даёт не геометрия, а «хвосты» шаблона/исходного семейства:
тип импорта DWG и его стили линий (остаются после удаления самой подложки), материалы с ресурсами внешнего вида,
вложенные семейства, образцы линий/штриховок, растровые изображения. Кроме того, Revit не уменьшает файл при обычном
сохранении. Кнопка **Purge** показывает список неиспользуемых элементов, удаляет их за несколько проходов
(как «Удалить неиспользуемое» несколько раз подряд) и предлагает сохранить семейство со сжатием, показывая размер
до/после. Журнал — в папке Logs (`…-purge.txt`).

## Конвейер Generate

1. **Проверка исходных данных** (галочка «Check source data…»). ИИ извлекает виды, размеры, габариты,
   технические характеристики и ищет нестыковки: сумма цепочки ≠ общему размеру (340 + 430 + 90 = 860 ≠ 850),
   разные значения одного размера на разных видах, размер не соответствует изображению, фото/эскиз противоречат
   чертежу. При нестыковках появляется окно **Source data check** — выберите значение, «Use recommended» или
   введите своё.
2. **Генерация** семейства с учётом анализа и ваших решений; техданные из PDF → параметры семейства:
   * Model / Manufacturer / Description / Type Comments / URL / Cost — во встроенные параметры Revit;
   * масса, производительность, мощность, напряжение, ток, частота, давление, температура — типизированные
     параметры с единицами (кг, м³/ч, кВт, В, А, Гц, Па, °C);
   * остальное (наименование, марка, ГОСТ, материал …) — текстовые параметры с исходными названиями.
3. **Проверка модели** (галочка «Verify the model…»): надстройка сама вычисляет габариты и положение каждого
   элемента по JSON (без Revit), сравнивает с габаритами из анализа и находит структурные ошибки; затем ИИ
   сверяет модель с исходными файлами и при необходимости исправляет её. Итог виден в предпросмотре в блоке
   «Check against source data».

Все промежуточные результаты сохраняются в `%AppData%\BeGenSt\RFAGen\Logs`: `source-analysis.json`,
`user-decisions.txt`, `generated-before-check.json`, `verification.json`, `check-report.txt`,
`…-final.json`, `…-generation-report.txt`.

Настройки: `%AppData%\BeGenSt\RFAGen\RFAGen.json` (при первом запуске ключи переносятся из старого FamFab).

## Сборка

1. **Закройте Revit.**
2. Откройте `C:\Portable\AI\BeGenSt\BeGenSt.slnx` в Visual Studio, конфигурация **Debug R24** (Revit 2024).
3. **Сборка → Пересобрать решение.** Post-build копирует `BeGenSt.addin` и DLL в
   `%AppData%\Autodesk\Revit\Addins\2024\` и **удаляет манифест старой надстройки** `ArchSmarterFamFab.addin`,
   чтобы не было двух вкладок. Предупреждение BEGENST01/02 в списке ошибок означает, что Revit был открыт и
   файлы не скопировались.
4. Запустите Revit, подтвердите загрузку надстройки BeGenSt.

Поддерживаются Revit 2022–2027 (R22–R24: .NET Framework 4.8; R25–R27: .NET 8/10).

## Проверочные файлы

* `TestDrawings\Cyclone_reference.json` — эталон циклона (высота цилиндра 770 = 340 + 430, патрубок 90,
  фланец входа на 800 мм от оси, спиральный вход, конус как тело вращения). Открывается через **Load JSON…**.
* `TestDrawings\Table_1200x600x750_GOST.pdf` — тестовый чертёж стола (3 вида, М1:10).

---

## English version

Revit add-in: an RFA family from a PDF drawing, a photo and a sketch using AI (Anthropic Claude, Google Gemini or Alibaba Qwen).
Based on the open-source ArchSmarter FamFab project (MIT, see LICENSE) and substantially reworked.

### User interface in Revit

Tab **BeGenSt** → panel **PDF&Pic2RFA**:

* **Purge** (first button) — family cleanup, see below.
* **Generate** — choose source files, analysis, generation, verification, 3D preview.
* **TypeGen** — a family with many types from a catalog table (see below).
* **Settings** — AI provider (Claude / Gemini / Qwen), API keys, model.

In the preview window the **RFAGen** button creates the family in the Family Editor, **Refine** edits the model by text.

### Source data

Up to **3 files of one object**: DWG/DXF or PDF (vector or scanned drawing with ≥ 2 views, technical data), JPG/JPEG (photo),
PNG (hand sketch). Priority when sources disagree: dimensions on the DWG/DXF or PDF > dimensions on the sketch > drawing
proportions > sketch > photo.

### DWG and DXF

The drawing is read inside the add-in with the ACadSharp library (MIT); neither AutoCAD nor a DWG to DXF conversion is needed. No provider
receives the DWG itself: the model gets an image of the sheet and exact data as text, so this works with Claude, Gemini and Qwen.

* The sheet (frame) is found and objects outside it are dropped; blocks are expanded; texts are decoded (including `\U+XXXX`).
* Each dimension is passed with its label and its drawn length. The label counts as the design value, views at their own scale are
  detected automatically, and differences within a view are flagged for the source data check.
* The data sent to the model is saved in the Logs folder (`…-cad-data.txt`).
* 2D only. 3D solids in a DWG and external references are not read; custom objects of other programs may be skipped
  (their number is reported to the model).

### Qwen (Alibaba Cloud Model Studio)

Replaces DeepSeek since version 1.2 (DeepSeek downscaled every image to ~1300×1300 px, and fine drawing details were
lost). Create the key in Model Studio → API Key (looks like `sk-…`) and enter it in **Settings** → *Alibaba Qwen API Key*.
Keys are bound to a region: the *Qwen endpoint* field must match the key's region
(Singapore — `https://dashscope-intl.aliyuncs.com/compatible-mode/v1` or
`https://<WorkspaceId>.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1`).

The Qwen API does not accept PDF, so the add-in **rasterizes the PDF locally** (pdfium, Docnet.Core): the first 3 pages
at high resolution (long side 3600 px, ≈9 MP; Qwen reads up to 16 MP per image), plus the text layer of a vector PDF
(dimensions, title block, technical data). Default model `qwen3.7-plus` (reasoning mode on, 16k token budget); the list
also has `qwen3.8-max`, `qwen3.7-flash`, `qwen3.6-plus`, `qwen3-vl-plus`.

**Via OpenRouter (e.g. the free `qwen/qwen3.8-27b:free`).** Models with "/" in the name are sent to OpenRouter
(`https://openrouter.ai/api/v1`) with the key from the *OpenRouter API Key* field (`sk-or-…`); the Qwen endpoint field
is not used for them. Free OpenRouter models have per-minute and per-day request limits — one generation = 2–3 requests.

### Purge — two modes

* **Purge unused elements** — like Revit's "Purge Unused" (several passes) + compact save.
* **Full clean before generation** — prepares an existing family for a new generation: removes geometry
  (solids and voids), connectors, nested families, dimensions, lines, reference lines, the family's own reference
  planes (template planes are kept), lookup tables and `size_lookup` formulas; then runs Purge. Family parameters are kept.
  TypeGen offers this mode itself when the family has such content.

### TypeGen — a family with types from a catalog

For catalog sheets with a type table (example — Avangard KZRr/KRr valves, DN15–300, PN16–40):

1. **Add files…** — the catalog sheet PDF (table + drawing), up to 3 files.
2. **1 · Read type table** — the AI extracts the "Overall and connection dimensions" table (merged PN cells are
   expanded into separate rows), the standard electric actuator and ΔP per DN, the figure table and notes. The table is
   editable; rows can be unchecked.
3. **Figure / Prefix / Type name pattern** — choosing a figure (25с947нж …) drops rows with a PN not allowed for the
   material; type names follow the pattern `{Prefix} {Designation} DN{DN} PN{PN}` → "КЗРр 25с947нж DN50 PN16".
4. **2 · Generate model** — the AI builds ONE parametric template (dimensions are expressions of D1, D2, D3, L, n, d, H, DN);
   the add-in checks every type (length = L, height = H); **3D preview** — any type.
5. **RFAGen** — creates the family: one type per row, type parameters from the table, "Description" per type,
   geometry in sets with visibility (equal sizes share a set), flange holes as an array of n,
   the actuator is hidden by the instance checkbox **"Показать_привод"** (Show actuator). After creation — automatic
   Purge and an offer to save compacted.
   * **Pipe connectors (DN) · Pipe Accessories** — two pipe connectors (Global system) at the ends ±L/2, diameter = DN
     of the selected type; the "Generic Models" family is switched to the "Pipe Accessories" category
     (part type "Valve – Insert").
   * **Lookup table (size_lookup by DN, PN)** — the table is embedded into the family as the lookup table **BGS_Catalog**
     (Manage → Lookup Tables); numeric type parameters (D1, D2, D3, L, d, H, n …) get formulas
     `size_lookup(BGS_Catalog, "Vi", …, DN, PN)`. Types are still created for every row. If a formula does not
     reproduce the table, the parameter keeps plain per-type values (see the report).

**Save / Load catalog JSON** — the catalog (template + table) is saved and loaded without an API key.
Ready example: `TestDrawings/Avangard_KZRr_25s947nzh_catalog.json` (40 types, 23 geometry sets, 27 template elements:
flanges with holes, conical branch pipes, globe body, bonnet with studs, yoke, stem, actuator with motor and handwheel).

### Family parameters and shared parameters (ФОП)

In the Generate window:

* **Fill the parameters of this family / template** — technical data and overall dimensions are written to the
  existing parameters of the open family/template (same names; nothing is duplicated, existing parameters are not
  deleted). The family language is detected automatically (from names of parameters, reference planes, views, levels):
  if it is Russian, new parameters and text values are created in Russian, otherwise in English.
* **Use parameters from a shared parameter file (ФОП)** — pick a shared parameters .txt file; data that matches by
  meaning is added to the family as **shared** parameters from that file (with their GUIDs) and filled with unit
  conversion. Revit's own shared parameter file setting is restored after generation.
* Data with no matching parameter is created as ordinary family parameters in the family's language.
* Mapping and translation are also applied to JSON loaded with **Load JSON…**: with an API key — by the selected
  AI model (a short text request), without a key — by a built-in term dictionary. Renamed parameters are also
  replaced in geometry expressions automatically.
* The **family type** gets the name specified in the add-in (Family name).
* **Description** is filled automatically: "Name" + technical characteristics from the source files
  (e.g. "Циклон ЦН-15. Масса: 120 кг; Производительность: 2500 м3/ч" — Cyclone TsN-15. Mass: 120 kg; Capacity: 2500 m³/h).

### Purge — file size

The large size of generated families (5–9 MB) usually comes not from geometry but from leftovers of the template/source
family: a DWG import type and its line styles (they remain after the underlay itself is deleted), materials with
appearance assets, nested families, line/fill patterns, raster images. Besides, Revit does not shrink the file on a
normal save. The **Purge** button lists unused elements, deletes them in several passes (like running "Purge Unused"
several times in a row) and offers to save the family compacted, showing the size before/after. Log — in the Logs
folder (`…-purge.txt`).

### Generate pipeline

1. **Source data check** ("Check source data…" checkbox). The AI extracts views, dimensions, overall sizes and
   technical characteristics and looks for inconsistencies: a dimension chain ≠ the overall dimension
   (340 + 430 + 90 = 860 ≠ 850), different values of the same dimension in different views, a dimension that does not
   match the image, a photo/sketch contradicting the drawing. If there are conflicts, the **Source data check** window
   appears — pick a value, "Use recommended" or enter your own.
2. **Generation** of the family taking the analysis and your decisions into account; technical data from the PDF →
   family parameters:
   * Model / Manufacturer / Description / Type Comments / URL / Cost — into Revit built-in parameters;
   * mass, capacity, power, voltage, current, frequency, pressure, temperature — typed parameters with units
     (kg, m³/h, kW, V, A, Hz, Pa, °C);
   * everything else (name, grade, GOST standard, material …) — text parameters with the original names.
3. **Model verification** ("Verify the model…" checkbox): the add-in itself computes the overall size and position of
   every element from the JSON (without Revit), compares them with the overall sizes from the analysis and finds
   structural errors; then the AI checks the model against the source files and fixes it if needed. The result is shown
   in the preview in the "Check against source data" block.

All intermediate results are saved to `%AppData%\BeGenSt\RFAGen\Logs`: `source-analysis.json`,
`user-decisions.txt`, `generated-before-check.json`, `verification.json`, `check-report.txt`,
`…-final.json`, `…-generation-report.txt`.

Settings: `%AppData%\BeGenSt\RFAGen\RFAGen.json` (on first run the keys are migrated from the old FamFab).

### Build

1. **Close Revit.**
2. Open `C:\Portable\AI\BeGenSt\BeGenSt.slnx` in Visual Studio, configuration **Debug R24** (Revit 2024).
3. **Build → Rebuild Solution.** The post-build step copies `BeGenSt.addin` and the DLLs to
   `%AppData%\Autodesk\Revit\Addins\2024\` and **deletes the manifest of the old add-in** `ArchSmarterFamFab.addin`,
   so that there are no two tabs. Warning BEGENST01/02 in the Error List means Revit was open and the files were
   not copied.
4. Start Revit and confirm loading of the BeGenSt add-in.

Revit 2022–2027 are supported (R22–R24: .NET Framework 4.8; R25–R27: .NET 8/10).

### Test files

* `TestDrawings\Cyclone_reference.json` — reference cyclone (cylinder height 770 = 340 + 430, branch pipe 90,
  inlet flange 800 mm from the axis, spiral inlet, cone as a revolution). Open it with **Load JSON…**.
* `TestDrawings\Table_1200x600x750_GOST.pdf` — test drawing of a table (3 views, scale 1:10).
