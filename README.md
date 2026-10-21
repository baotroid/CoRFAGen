# BeGenSt · PDF&Pic2RFA · RFAGen

Revit add-in (Revit 2024–2027) that builds a Revit family from a 2D PDF drawing, a photo and/or a sketch
with AI (Anthropic Claude, Google Gemini, Alibaba Qwen / OpenRouter), checks the model against the sources,
shows a 3D preview and writes the family. **TypeGen** builds a family with many types from a catalog table
(e.g. valves DN15–300 × PN16–40: types, lookup table, pipe connectors, actuator toggle). Includes **Purge**
(unused elements + compact save, or a full clean before a new generation) and mapping of technical data to
family / shared parameters (ФОП) in the family's language.

* Documentation (Russian + English): [README_RU.md](README_RU.md#english-version)
* Development history (Russian + English): [docs/HISTORY_RU.md](docs/HISTORY_RU.md#english-version)
* For AI assistants: [CLAUDE.md](CLAUDE.md)
* Claude skill (family JSON from drawings without an API key): [skills/rfagen-json-from-drawing](skills/rfagen-json-from-drawing/SKILL.md)
* Installer: see **Releases** (`BeGenSt_PDFPic2RFA_<version>.msi`)

Based on ArchSmarter FamFab (MIT, see [LICENSE](LICENSE)).
