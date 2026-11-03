# CoRFAGen: plug-in for Autodesk® Revit®

CoRFAGen is a plug-in for Autodesk Revit 2024–2027 (formerly BeGenSt PDF&Pic2RFA) that builds a Revit family from a 2D DWG/DXF or PDF drawing, a photo and/or a sketch
with AI (Anthropic Claude, Google Gemini, Alibaba Qwen / OpenRouter), checks the model against the sources,
shows a 3D preview and writes the family. **TypeGen** builds a family with many types from a catalog table
(e.g. valves DN15–300 × PN16–40: types, lookup table, pipe connectors, actuator toggle). Includes **Purge**
(unused elements + compact save, or a full clean before a new generation) and mapping of technical data to
family / shared parameters (ФОП) in the family's language.

* Documentation (Russian + English): [README_RU.md](README_RU.md#english-version)
* Development history (Russian + English): [docs/HISTORY_RU.md](docs/HISTORY_RU.md#english-version)
* For AI assistants: [CLAUDE.md](CLAUDE.md)
* Claude skill (family JSON from drawings without an API key): [skills/corfagen-json-from-drawing](skills/corfagen-json-from-drawing/SKILL.md)
* Installer: see **Releases** (`CoRFAGen_<version>.msi`)

Based on ArchSmarter FamFab (MIT, see [LICENSE](LICENSE)). DWG/DXF reading: [ACadSharp](https://github.com/DomCR/ACadSharp), MIT, Copyright (c) 2026 Albert Domenech.

Autodesk and Revit are registered trademarks or trademarks of Autodesk, Inc., and/or its subsidiaries and/or affiliates in the USA and/or other countries. CoRFAGen is an independent product and is not affiliated with, endorsed or sponsored by Autodesk, Inc.
