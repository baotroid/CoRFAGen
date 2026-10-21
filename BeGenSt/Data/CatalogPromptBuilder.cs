using System.Text;

namespace BeGenSt.Data
{
    /// <summary>Prompts of the TypeGen pipeline: 1) read the catalog tables, 2) build one parametric template.</summary>
    public static class CatalogPromptBuilder
    {
        // ------------------------------------------------------------------ 1. tables

        public const string ExtractSystemPrompt = @"You read manufacturer catalog sheets (PDF) of building / process equipment (valves, fittings, pumps,
fans, dampers ...) and extract the TABLE OF TYPE SIZES used to build a Revit family with several types.
Return ONE JSON object only (no markdown):
{
  ""product_name"": ""full product name in the sheet language, e.g. Клапан запорно-регулирующий односедельный с ЭИМ"",
  ""manufacturer"": ""..."", ""url"": ""..."",
  ""key_columns"": [""DN"", ""PN""],
  ""columns"": [ { ""name"": ""DN"", ""type"": ""integer|number|length|angle|mass|pressure|text"", ""unit"": ""mm|кг|кгс/см2|МПа|"", ""description"": ""meaning"" } ],
  ""types"": [ { ""DN"": 50, ""PN"": 16, ""D1"": 99, ... } ],
  ""figures"": [ { ""figure"": ""25с947нж"", ""prefix"": ""КЗРр"", ""material"": ""Сталь 25Л"", ""seal"": ""нж"", ""pn"": [16, 25, 40] } ],
  ""constants"": { ""Материал корпуса"": ""..."", ""Присоединение"": ""..."" },
  ""notes"": [ ""footnotes and remarks that matter for the types"" ],
  ""warnings"": [ ""anything ambiguous you had to decide"" ]
}
RULES
1. One object in ""types"" = one row of the main dimension table (""Габаритные и присоединительные размеры"" or similar).
   Keep the table's column names exactly (D1, D2, D3, L, n, d, H, Масса ...). Numbers as numbers (decimal point), no footnote marks.
2. MERGED CELLS: expand them. A cell ""16 / 25 / 40"" or ""25, 40"" in the PN column means separate rows for each PN with the same
   values; a DN cell spanning several PN rows repeats the DN.
3. Join the other tables of the sheet by the key column (DN): e.g. the standard actuator and the allowed pressure drop at that actuator
   become text columns (""Стандартный ЭИМ"", ""ΔP при стандартном ЭИМ, МПа""). Footnotes that apply to a row go into a text column ""Примечание"".
4. Figures / designations table (материальное исполнение): one object per figure with its material, seal and the PN values it allows.
   ""prefix"" = the product abbreviation of that figure (if two are given like ""КЗРр/КРр"", give the first).
5. Lengths in millimetres. Do not invent rows or values; if a value is missing use null and add a warning.";

        public static string BuildExtractUserText(IList<InputAttachment> attachments, string userContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine(FamilyPromptBuilder.DescribeFiles(attachments));
            sb.AppendLine("Extract the type-size table(s) of the product on these sheets and return the JSON described in your instructions.");
            if (!string.IsNullOrWhiteSpace(userContext))
            {
                sb.AppendLine();
                sb.AppendLine("USER NOTES: " + userContext.Trim());
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ 2. template

        public static string BuildTemplateSystemPrompt(string skillPrompt, string schemaJson)
        {
            return FamilyPromptBuilder.BuildSystemPrompt(skillPrompt, schemaJson) + @"

## CURRENT TASK: PARAMETRIC TEMPLATE FOR A FAMILY WITH SEVERAL TYPES (TypeGen)

You create ONE family JSON whose geometry is valid for EVERY row of the type table. The add-in evaluates the template once per
distinct row and builds one geometry set per row, so:
1. Every catalog column that describes size (DN, D1, D2, D3, L, n, d, H ... — exact names from the table) must be a parameter
   (instance_or_type ""type"") and ALL geometry dimensions must be expressions of these parameters. No fixed sizes except small
   constants (gaps, raised-face height 2–3 mm). Use only + - * / and parentheses (no functions, no IIF).
2. Proportions that the table does not give (body height, bonnet, yoke, actuator) are expressions of the given sizes
   (e.g. ""0.36*D3"", ""0.58*H"") chosen so that the shape is plausible for the SMALLEST and the LARGEST row.
3. Overall sizes must match the table exactly: face-to-face length L along X (pipe axis on X at height 0, centred),
   total height H measured as given on the drawing (usually from the pipe axis to the top of the actuator).
4. Flanges: outer circle D3 (or the flange diameter column), raised face D1, bolt holes with ""holes"": [ { ""radius"": ""d/2"",
   ""pattern"": { ""count"": ""n"", ""radius"": ""D2/2"", ""start_angle"": ""180/n"" } } ] on the flange extrusion; the bore as a void
   extrusion of diameter DN through the whole length.
5. DETAIL LEVEL — recognisable, not a block model (25–35 elements). For a globe / control valve, for example:
   raised faces + flanges with bolt holes; conical NECKS from each flange to the body (""blend"" circle → circle);
   globe body as two cones (""blend"" on the horizontal plane through the axis: lower cone up to 0, upper cone from 0);
   body/bonnet flanges; bonnet as a cone; bonnet studs as ONE element with an element-level polar array
   ""pattern"": { ""count"": 8, ""radius"": ""0.265*D3"", ""start_angle"": 22.5 } (copies the element around the plane origin);
   gland; yoke = two pillars (one cylinder + ""pattern"" count 2) + top plate; stem; stem coupling.
   Every blend: bottom_offset < top_offset, both profiles circles with the same centre.
6. Actuator / drive (e.g. AUMA-like): flange, gearbox box, motor cylinder sideways (+ fan cover), handwheel ring on the other
   side (extrusion with a central hole in ""holes"") + shaft, control box, position indicator; all with ""toggle"": ""actuator"",
   the top exactly at H. Use subcategories ""Body"" and ""Actuator"".
   Check every expression for the smallest and the largest row: no element may have zero or negative length, parts must not
   collide (body top < yoke < actuator bottom).
6a. Pipe connectors are added by the add-in at x = ±L/2 (diameter DN) — do NOT model them; the end faces of the raised faces
   must lie exactly at ±L/2.
7. Non-geometric data (mass, actuator model, ΔP, notes, product name, manufacturer, materials) are parameters too; text in the
   language of the catalog. Use ""builtin"" for Model, Manufacturer, URL.
8. Default values of the parameters = the row given as REPRESENTATIVE below.
Return ONLY the family JSON (without a ""catalog"" section — the add-in adds the table).";
        }

        public static string BuildTemplateUserText(IList<InputAttachment> attachments, string extractionJson,
            string representativeRow, string smallestRow, string largestRow, string language, string userContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine(FamilyPromptBuilder.DescribeFiles(attachments));
            sb.AppendLine("TABLE EXTRACTED FROM THE CATALOG (columns, figures, notes; rows shortened):");
            sb.AppendLine("```json");
            sb.AppendLine(extractionJson);
            sb.AppendLine("```");
            sb.AppendLine("REPRESENTATIVE ROW (defaults): " + representativeRow);
            sb.AppendLine("SMALLEST ROW: " + smallestRow);
            sb.AppendLine("LARGEST ROW: " + largestRow);
            sb.AppendLine($"Family language: {language}. Names of new parameters and text values in {language}; " +
                          "keep the table column names (D1, L, H ...) unchanged.");
            sb.AppendLine("Build the parametric family JSON now, following the drawing of the product on the sheet " +
                          "(body shape, flanges, bonnet, actuator).");
            if (!string.IsNullOrWhiteSpace(userContext)) sb.AppendLine("USER NOTES: " + userContext.Trim());
            return sb.ToString();
        }
    }
}
