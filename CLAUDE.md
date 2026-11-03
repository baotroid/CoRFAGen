# CLAUDE.md — CoRFAGen (Revit add-in)

Guidance for Claude Code (and Claude in Cowork) working on this repository.
The user writes in Russian — answer in Russian. UI strings of the add-in are in **English**.

## What it is

CoRFAGen (formerly BeGenSt PDF&Pic2RFA) is a plug-in for Autodesk Revit (tab **CoRFAGen**, panel **Family Generator**) that builds a Revit family (RFA) from up to 3 source files
of one object — a 2D DWG/DXF or PDF drawing (≥ 2 views, title block, technical data), a JPG photo and/or a PNG sketch — using an
AI model, checks the result and shows a 3D preview before writing geometry into the open family document.
Derived from the open ArchSmarter **FamFab** add-in (MIT, see `LICENSE`), heavily reworked.

Ribbon buttons (left → right): **Purge**, **Generate**, **TypeGen**, **Settings**. In the preview window **CoRFAGen** writes the
family, **Refine** edits the model by text.

## Repository layout

```
CoRFAGen.slnx                 solution (configs Debug/Release R22..R27)
CoRFAGen/CoRFAGen.csproj       multi-target add-in project
  App.cs                     ribbon (Purge, Generate, TypeGen, Settings)
  CmdPreGenerate.cs          "Generate" command: window → pipeline → preview → FamilyGenerator
  CmdPurge.cs                "Purge": unused elements + compact save, or Full clean before generation (FamilyCleaner)
  CmdSettings.cs             settings window
  CmdTypeGen.cs              "TypeGen": catalog table → family with many types (UI/TypeGenWindow)
  Data/
    LlmClient.cs             ILlmClient, LlmProviders (Claude, Gemini, Qwen), LlmClientFactory, LlmHttp helpers
    ClaudeClient.cs          Anthropic Messages API (PDF as document blocks)
    GeminiClient.cs          Gemini generateContent (PDF inline_data)
    QwenClient.cs            OpenAI-compatible chat/completions: Alibaba Model Studio (Qwen) or OpenRouter
                             (model ids containing "/" go to OpenRouter with the OpenRouter key)
    PdfRasterizer.cs         PDF → PNG pages (+ text layer) via Docnet.Core/pdfium for providers without PDF input
    CadDrawingReader.cs      DWG/DXF (ACadSharp) → sheet PNG + exact data text: sheet frame, blocks, labels vs drawn length, view scales
    FamilyAiService.cs       Analyze / Generate / Verify / Refine / SendTextAsync facade
    FamilyPromptBuilder.cs   prompts; GenerationPipeline.cs (analyse → conflicts → generate → verify)
    SourceAnalysis.cs, GeometryInspector.cs   conflict model, JSON-only geometry check
    FamilyGenerator.cs       JSON → Revit family (ref planes, extrusions, blends, revolutions, sweeps, voids)
    TechnicalParameters.cs   parameters: built-ins, typed tech data (mass, kW, m3/h…), reuse of existing /
                             shared (ФОП) parameters, Description = Наименование + characteristics
    ParameterTargets.cs      collects family params, detects template language (ru/en), parses ФОП .txt
    ParameterMapper.cs       maps/translates JSON parameters to family/ФОП params (AI, or built-in dictionary)
    CatalogJson.cs           catalog JSON: types, geometry sets, name pattern, single-type instantiation
    FamilyGenerator.Catalog.cs  types + visibility sets (BGS_Set, BGS_Vis_NN), actuator toggle
    FamilyGenerator.CatalogMep.cs  pipe connectors (±L/2, Ø = DN), Pipe Accessories category, lookup table BGS_Catalog
    CatalogPromptBuilder.cs  prompts: read type table, parametric template
    FamilyPurger.cs          GetUnusedElements (2024+) / PerformanceAdviser (≤2023), multi-pass delete
    FamilyCleaner.cs         full clean: geometry, connectors, nested families, dimensions, lines, own ref planes,
                             lookup tables + size_lookup formulas (parameters kept)
    FailureLog.cs            IFailuresPreprocessor: Revit errors/warnings with names of generated elements → report
    CoRFAGenSettings*.cs       settings in %AppData%\CoRFAGen\CoRFAGen.json, logs in ...\Logs
  UI/                        WPF windows (dark grey + orange/yellow/white styles in CoRFAGenStyles.xaml)
  EmbeddedContent/           SKILL.md (system prompt), family-schema.json, revit-family-viewer.html (Three.js)
  Resources/                 ribbon icons 16/32 px (pregen = Generate, purge, settings-sliders)
Installer/                   WiX v5 MSI (per-machine, Revit 2024–2027 features)
BuildInstaller.cmd           builds Release R24..R27 + MSI → Setup\CoRFAGen_<VER>.msi, log Setup\build.log
TestDrawings/                Cyclone_reference.json (reference family), Table_1200x600x750_GOST.pdf
skills/corfagen-json-from-drawing/   Claude skill: make the family JSON from drawings without an API key
docs/                        HISTORY_RU.md (development history & decisions), model comparison
```

## Build

* Requirements: Windows, .NET SDK 10 (VS 2026), internet for NuGet. Revit must be **closed** for Debug builds
  (post-build copies DLLs to `%AppData%\Autodesk\Revit\Addins\<year>`; warnings CORFAGEN01/02 = files locked).
* One config: `dotnet build CoRFAGen\CoRFAGen.csproj -c "Debug R24"`.
* Release + MSI: double-click `BuildInstaller.cmd` (bump `VER` first — MajorUpgrade replaces older versions).
  Check `Setup\build.log` (cp866 console encoding).
* Targets: R22–R24 `net48`, R25/R26 `net8.0-windows`, R27 `net10.0-windows`.
  `Revit_All_Main_Versions_API_x64` = `$(RevitVersion).*`, pinned to `2026.0.0` for R26 (CS1705 otherwise).
* `SkipDeploy=true` disables the AppData deploy (used by the MSI build).

## Gotchas (learned the hard way)

* net48: needs `System.Net.Http` reference + `System.Text.Json` 8.0.5 + CopyLocalLockFileAssemblies; TLS 1.2 is forced
  under `#if REVIT2020..2024`; assembly resolve from the add-in folder in `App.cs`.
* Use fully-qualified `System.Windows.Visibility`, `System.Windows.MessageBox` (Revit DB has same names).
* `BuiltInParameter.UNIFORMAT_CODE` does not exist in 2026/2027 → `#if !(REVIT2026 || REVIT2027)`.
* `ElementId.IntegerValue` (≤2023) vs `.Value` (2024+) → use `IntegerValueCompat()` (ParameterTargets.cs).
* pdfium.dll is copied next to CoRFAGen.dll by target `CopyPdfiumNative`; PdfRasterizer preloads it and refuses to
  run if another pdfium.dll is already loaded in Revit.
* Revit does not shrink .rfa on normal save — Purge offers `SaveOptions { Compact = true }`.
* Russian templates: ref planes "Центр (слева/справа)" etc. are aliased to the English names used by the JSON.
* Catalog JSON = family JSON + `catalog` {type_name_pattern, constants, columns, types, toggles}; extrusions may have
  `holes` (polar `pattern`), elements may have `toggle`. Example: `TestDrawings/Avangard_KZRr_25s947nzh_catalog.json`.
* Catalog options: `catalog.lookup` {enabled, keys} and `catalog.connectors` {enabled, system, length_parameter,
  diameter_parameter}. Revit formulas written by the add-in avoid unit suffixes and decimals (localized Revit):
  BGS_Zero is the length default of size_lookup, set visibility = `and(BGS_Set > k-1, BGS_Set < k+1)`.
* Catalog generation: existing column parameters are normalized (formula removed, instance → type, reporting →
  normal); lookup table + size_lookup formulas run in a SECOND transaction after the main one (verified per type,
  fallback to plain values). Generating into a family with its own content (nested families, own lookup tables,
  dimensions) breaks types → TypeGen offers Full clean first.
* Element-level `pattern` (polar array) is expanded in FamilyGenerator.ExpandPattern and CatalogJson.ExpandPatterns.
* Expression substitution is by whole identifier (short names n, d, L, H) — keep it that way.
* JSON schema `0.1`: parameters may carry `"target": "existing" | "shared" | "new"`; expressions reference
  parameter names with spaces → underscores.
* DWG/DXF: an InputAttachment with `ExtractedText`; Bytes = rendered PNG, so clients treat it as an image; the text goes in through
  `FamilyPromptBuilder.DescribeFiles`. The drawn block of a dimension nested in a block has its own offset (align it by the label).
  A copy of the reader is kept in `skills/corfagen-json-from-drawing/dwg_extract` (copied on build; do not edit the copy).
* ACadSharp on net48 asks for System.Memory 4.0.5; `App.ResolveFromAddinFolder` returns whatever version is already loaded (pyRevit and others
  load 4.0.1.x). Tested in the Revit 2024 process: it works. A clean net48 process does not reproduce this, so test such dependencies inside Revit.
* Client drawings stay local: `TestDrawings/*.dwg|*.dxf`, their PDFs and `*_from_DWG*` are git-ignored. Do not commit them.
* Test generation on a new family document only: the Revit session usually has a live project open (see the skill, section 5).
* Rename to CoRFAGen (1.6.0), kept on purpose: installer `UpgradeCode` (replaces older versions) and add-in `ClientId`; `BGS_*` names, already stored in
  generated families; `BeGenSt.addin` in the installer and post-build cleanup; migration of `%AppData%\BeGenSt\RFAGen\RFAGen.json` to `%AppData%\CoRFAGen\CoRFAGen.json`;
  old rows of `docs/HISTORY_RU.md` and past release notes; `LICENSE` and the FamFab attribution.
* In public texts name the host as "plug-in for Autodesk Revit" and keep the trademark notice (README files, MSI comments).
* Never log or echo API keys; keys live only in the user's CoRFAGen.json. `LlmHttp.SanitizeKey` strips invisible chars.

## Workflow / sync rules

* `main` is the source of truth. The working copy on the user's PC is `C:\Portable\AI\BeGenSt` (the folder keeps its old name unless the user renames it).
* Claude (Cowork) may edit files directly in that folder. **Before starting work: `git pull`. After a change set:
  build, then commit with a clear message (Russian or English) and `git push`.**
* Do not commit: `bin/`, `obj/`, `.vs/`, `Setup/*.msi`, `Setup/build.log`, Office lock files `~$*`.
  Release MSIs go to **GitHub Releases** (tag `v<VER>`), not into git.
* Bump `VER` in `BuildInstaller.cmd` for every installer handed to users; add a line to `docs/HISTORY_RU.md`.
