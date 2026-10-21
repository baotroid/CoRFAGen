using System.Text;

namespace BeGenSt.Data
{
    /// <summary>
    /// Builds the prompts shared by all providers, so Claude and Gemini receive identical instructions.
    /// Pipeline: 1) analyse sources (dimensions, technical data, conflicts) → 2) generate family JSON
    /// → 3) verify the JSON against the sources (with an automatic geometry report) → preview.
    /// </summary>
    public static class FamilyPromptBuilder
    {
        private const string OutputFormat =
            "\n\n## Output Format\n\nReturn ONLY the raw JSON object. Do not wrap it in markdown code fences. " +
            "Do not include any explanation before or after the JSON. The response must start with `{` and end with `}`.";

        public static string BuildSystemPrompt(string skillPrompt, string schemaJson)
        {
            return skillPrompt +
                   "\n\n## JSON Schema\n\nThe generated JSON must conform to this schema:\n\n```json\n" + schemaJson + "\n```" +
                   OutputFormat;
        }

        public static bool ContainsDrawings(IList<InputAttachment> attachments)
        {
            return attachments != null && attachments.Any(a => a.IsPdf);
        }

        // ------------------------------------------------------------------ file roles

        public static string DescribeFiles(IList<InputAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0) return "No source files.";
            var sb = new StringBuilder();
            sb.AppendLine($"{attachments.Count} source file(s) of ONE object are attached, in this order:");
            for (int i = 0; i < attachments.Count; i++)
                sb.AppendLine($"- File {i + 1}: \"{attachments[i].FileName}\" — {RoleOf(attachments[i])}");
            sb.AppendLine("Priority when sources disagree: dimensions written on the PDF drawing > dimensions written on the sketch > " +
                          "proportions of the drawing > sketch > photo. The photo defines appearance, details and details hidden in the drawing. " +
                          "If a file obviously has a different role than assumed (e.g. the PNG is a photo), treat it by its real content.");
            return sb.ToString();
        }

        public static string RoleOf(InputAttachment a)
        {
            if (a.IsPdf) return "PDF: 2D technical drawing / datasheet (vector or scanned): orthographic views, dimensions, title block, technical data";
            if (a.MimeType == "image/png") return "PNG: usually a hand-drawn sketch (shape intent; dimensions written on it are valid)";
            return "JPG/JPEG: usually a photo of the real object (appearance, proportions, visible details)";
        }

        // ------------------------------------------------------------------ 1. analysis

        public static string BuildAnalysisSystemPrompt(string skillPrompt)
        {
            return skillPrompt + @"

## CURRENT TASK: SOURCE DATA ANALYSIS (do NOT generate the family yet)

Read all source files and return an analysis JSON that the add-in shows to the user before generation.

1. Identify the object, every view on every page (see Orthographic Drawing Flow) and the units.
2. List every dimension you can read (value, what it measures, axis, view). Mark scaled/assumed values.
3. Determine the overall size of the object: width along X, depth along Y, height along Z (vertical)
   in the family coordinate system (front view looks from −Y, Z is up).
4. Extract ALL technical data written in the files (title block, tables, notes, datasheet text):
   name/designation, model, manufacturer, mass, capacity/air flow, power, voltage, current, frequency,
   pressure, temperature, material, standard/GOST, etc. Keep the original wording of names and values.
5. CONSISTENCY CHECK — this is the most important part. Look for contradictions and put each one in
   ""conflicts"":
   - a chain of dimensions whose sum differs from the overall dimension over the same span
     (e.g. 340 + 430 + 90 = 860 but the overall dimension says 850);
   - the same feature dimensioned differently in two views, or a diameter in one view that does not
     match the width in another view;
   - a dimension that contradicts the drawn geometry by more than ~10 % (drawing not to scale);
   - technical data that contradicts the geometry (e.g. flange DN vs drawn diameter);
   - photo/sketch showing features that contradict the drawing (extra/missing parts, different count).
   For every conflict give 2–4 concrete options (the candidate values, each with a short explanation),
   and the index of the option you recommend. Do NOT report missing dimensions as conflicts — list them
   in ""assumptions"" instead. If there are no contradictions, return an empty ""conflicts"" array.

Return exactly this JSON structure (all lengths in the drawing units):
{
  ""object"": { ""name"": ""..."", ""type"": ""..."", ""category"": ""Specialty Equipment"" },
  ""units"": ""millimeters"",
  ""files"": [ { ""file"": ""name"", ""role"": ""drawing|photo|sketch|datasheet"", ""notes"": ""..."" } ],
  ""views"": [ { ""file"": ""name"", ""page"": 1, ""view"": ""front|plan|left|right|rear|section|detail|view A"", ""notes"": ""..."" } ],
  ""dimensions"": [ { ""id"": ""D1"", ""value"": 900, ""measures"": ""cylinder outer diameter"", ""axis"": ""x|y|z|diameter|radius|angle"", ""view"": ""front"", ""source"": ""dimension|scaled|photo|sketch|assumed"" } ],
  ""overall"": { ""width_x"": 0, ""depth_y"": 0, ""height_z"": 0 },
  ""technical_data"": [ { ""name"": ""Масса"", ""value"": ""120"", ""unit"": ""kg"", ""suggested_type"": ""mass|hvac_power|electrical_power|voltage|current|frequency|air_flow|pipe_flow|pressure|temperature|text|number"", ""builtin"": ""Model|Manufacturer|Description|Type Comments|URL|Cost|Keynote|Assembly Code|"", ""source"": ""title block"" } ],
  ""conflicts"": [ { ""id"": ""C1"", ""question"": ""Height from the bottom of the cylinder to the top of the outlet flange: which value?"", ""affects"": ""Cylinder Height"", ""options"": [ { ""label"": ""850 — overall dimension on the drawing"", ""value"": ""850"" }, { ""label"": ""860 — sum of the chain 340 + 430 + 90"", ""value"": ""860"" } ], ""recommended"": 0 } ],
  ""assumptions"": [ ""..."" ]
}" + OutputFormat;
        }

        public static string BuildAnalysisUserText(IList<InputAttachment> attachments, string userContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine(DescribeFiles(attachments));
            sb.AppendLine("Analyse the sources and return the analysis JSON (not the family).");
            AppendContext(sb, userContext);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ 2. generation

        public static string BuildGenerateUserText(IList<InputAttachment> attachments, string userContext,
            string analysisJson = null, string decisionsText = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(DescribeFiles(attachments));

            if (ContainsDrawings(attachments))
            {
                sb.AppendLine("Follow the \"Orthographic Drawing Flow\" of your instructions: identify the views, map them to the family " +
                              "axes (plan = X/Y, front = X/Z, side = Y/Z), use the dimension values written on the drawing exactly, " +
                              "reconstruct geometry that matches ALL views, run \"Step 7: Self-Check\" and apply the " +
                              "\"Process Equipment Patterns\" when relevant.");
            }
            else
            {
                sb.AppendLine("Analyze the attached image(s). If an image is an orthographic drawing, follow the \"Orthographic Drawing Flow\".");
            }

            if (!string.IsNullOrWhiteSpace(analysisJson))
            {
                sb.AppendLine();
                sb.AppendLine("SOURCE ANALYSIS (already checked; use these dimensions and technical data):");
                sb.AppendLine("```json");
                sb.AppendLine(analysisJson);
                sb.AppendLine("```");
            }

            if (!string.IsNullOrWhiteSpace(decisionsText))
            {
                sb.AppendLine();
                sb.AppendLine("USER DECISIONS ON CONFLICTING DATA (mandatory — they override the drawing and your own choice; " +
                              "adjust the related dimensions so that the chain stays consistent):");
                sb.AppendLine(decisionsText);
            }

            sb.AppendLine();
            sb.AppendLine("TECHNICAL DATA → PARAMETERS: create one family parameter for every item of technical data found in the sources " +
                          "(name, model, manufacturer, mass, capacity, power, voltage, current, pressure, material, standard ...). " +
                          "Use the typed parameter types from the schema (mass, electrical_power, air_flow, ...) with the \"unit\" field, " +
                          "\"text\" for everything else, group \"Identity Data\", \"Mechanical\" or \"Electrical\". " +
                          "Use \"builtin\" for Model, Manufacturer, Description, Type Comments, URL, Cost. " +
                          "Technical-data parameters keep their original names (e.g. Russian); geometric parameters used in expressions " +
                          "must have Latin names.");
            sb.AppendLine();
            sb.AppendLine("Generate the Revit family JSON definition. Use Standard detail level. Where the sources give no dimension, " +
                          "propose reasonable defaults. Do not ask questions — generate the JSON directly.");
            AppendContext(sb, userContext);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ 3. verification

        public static string BuildVerifySystemPrompt(string skillPrompt, string schemaJson)
        {
            return skillPrompt +
                   "\n\n## JSON Schema\n\nThe family JSON must conform to this schema:\n\n```json\n" + schemaJson + "\n```" + @"

## CURRENT TASK: VERIFY THE GENERATED FAMILY AGAINST THE SOURCE FILES

You receive the source files, the source analysis, the user's decisions, the generated family JSON and
an automatic geometry report (evaluated bounding boxes of every element, overall extents, detected
structural problems). Check, element by element:
1. Overall extents equal the overall dimensions of the object (and the user decisions).
2. Every dimension from the analysis is realised by a parameter / geometry with exactly that value.
3. Every element is where the drawing shows it (zone check), nothing extra, nothing missing,
   body stack continuous (no gaps / overlaps between consecutive parts).
4. Views agree: the shape seen in plan, front and side views matches the model.
5. Technical data from the sources is present as parameters with correct values and units.
6. Every problem in the automatic geometry report is resolved.

If everything is correct return {""status"": ""ok"", ""issues"": []}.
Otherwise fix the family and return
{""status"": ""fixed"", ""issues"": [ { ""severity"": ""high|medium|low"", ""description"": ""what was wrong and how it was fixed"" } ], ""family"": { ...the COMPLETE corrected family JSON... }}" +
                   OutputFormat;
        }

        public static string BuildVerifyUserText(IList<InputAttachment> attachments, string userContext,
            string analysisJson, string decisionsText, string familyJson, string geometryReport)
        {
            var sb = new StringBuilder();
            sb.AppendLine(DescribeFiles(attachments));
            if (!string.IsNullOrWhiteSpace(analysisJson))
            {
                sb.AppendLine("SOURCE ANALYSIS:");
                sb.AppendLine("```json"); sb.AppendLine(analysisJson); sb.AppendLine("```");
            }
            if (!string.IsNullOrWhiteSpace(decisionsText))
            {
                sb.AppendLine("USER DECISIONS (mandatory):");
                sb.AppendLine(decisionsText);
            }
            sb.AppendLine("GENERATED FAMILY JSON:");
            sb.AppendLine("```json"); sb.AppendLine(familyJson); sb.AppendLine("```");
            sb.AppendLine("AUTOMATIC GEOMETRY REPORT (computed by the add-in from the JSON above):");
            sb.AppendLine(geometryReport);
            sb.AppendLine("Verify the family against the source files and return the verification JSON.");
            AppendContext(sb, userContext);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ refine

        public static string BuildRefineUserText(string currentJson, string userInstruction, IList<InputAttachment> attachments)
        {
            string sourceNote = "";
            if (attachments != null && attachments.Count > 0)
            {
                sourceNote = DescribeFiles(attachments) +
                             "Keep the geometry consistent with every view and with the dimensions written on the drawing.\n\n";
            }

            return sourceNote +
                   "Here is the current Revit family JSON definition:\n\n```json\n" + currentJson + "\n```\n\n" +
                   "Modify this family definition according to the following instruction. Return the complete updated JSON:\n\n" +
                   userInstruction;
        }

        private static void AppendContext(StringBuilder sb, string userContext)
        {
            if (string.IsNullOrWhiteSpace(userContext)) return;
            sb.AppendLine();
            sb.AppendLine("Additional context from the user (takes priority over your assumptions):");
            sb.AppendLine(userContext);
        }
    }
}
