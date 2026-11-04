using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using C = System.Windows.Controls;
using System.Windows.Data;
using RevitDialog = Autodesk.Revit.UI.TaskDialog;

namespace CoRFAGen.Data
{
    /// <summary>
    /// Interface language. The English text stays in the code and in XAML; in Russian every finished line is looked up
    /// in LocDictionary, exactly or by a pattern with values ({0}), so a line without a translation simply stays English.
    /// Only the screen is translated: requests to the AI and report files keep their English text.
    /// The name CoRFAGen is not translated, also on the button that carries it.
    /// </summary>
    public static class Loc
    {
        public const string English = "en";
        public const string Russian = "ru";

        public static string Language { get; private set; } = English;
        public static bool IsRussian => Language == Russian;

        public static void SetLanguage(string language) => Language = language == Russian ? Russian : English;

        private static readonly Dictionary<string, string> Exact = new Dictionary<string, string>();
        private static readonly List<(Regex Pattern, string Russian, bool[] Nested)> Patterns = new List<(Regex, string, bool[])>();
        private static readonly Regex Slot = new Regex(@"\{(m?)(\d)\}");
        private static readonly Regex LinePrefix = new Regex(@"^(\s*(?:[⚠•✓]\s*)?)(.*?)(\s*)$", RegexOptions.Singleline);
        private static bool _loaded;

        /// <summary>Built on first use in Russian, so an English session does not pay for it at Revit start.</summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            var patterns = new List<(string En, string Ru)>();
            foreach (KeyValuePair<string, string> kv in LocDictionary.Russian)
            {
                if (Slot.IsMatch(kv.Key)) patterns.Add((kv.Key, kv.Value));
                else Exact[kv.Key] = kv.Value;
            }
            // more specific patterns first: the longer the fixed text, the earlier it is tried
            foreach (var p in patterns.OrderByDescending(p => Slot.Replace(p.En, "").Length))
                Patterns.Add(Compile(p.En, p.Ru));
        }

        private static (Regex, string, bool[]) Compile(string en, string ru)
        {
            var nested = new bool[10];
            int at = 0;
            var rx = new System.Text.StringBuilder("^");
            foreach (Match m in Slot.Matches(en))
            {
                rx.Append(Regex.Escape(en.Substring(at, m.Index - at)));
                int n = m.Groups[2].Value[0] - '0';
                rx.Append("(?<v").Append(n).Append(">.*?)");
                nested[n] = m.Groups[1].Value == "m";
                at = m.Index + m.Length;
            }
            rx.Append(Regex.Escape(en.Substring(at))).Append('$');
            return (new Regex(rx.ToString(), RegexOptions.Singleline), ru, nested);
        }

        /// <summary>The text in the current language, line by line.</summary>
        public static string T(string text)
        {
            if (!IsRussian || string.IsNullOrEmpty(text)) return text;
            EnsureLoaded();
            return string.Join("\n", text.Split('\n').Select(l => Line(l.TrimEnd('\r'), 0)));
        }

        private static string Line(string line, int depth)
        {
            if (string.IsNullOrWhiteSpace(line)) return line;
            Match parts = LinePrefix.Match(line);
            string body = parts.Groups[2].Value;
            if (body.Length == 0) return line;
            if (Exact.TryGetValue(body, out string exact)) return parts.Groups[1].Value + exact + parts.Groups[3].Value;
            if (depth > 3) return line;
            foreach (var (pattern, russian, nested) in Patterns)
            {
                Match m = pattern.Match(body);
                if (!m.Success) continue;
                string done = Slot.Replace(russian, s =>
                {
                    int n = s.Groups[2].Value[0] - '0';
                    string value = m.Groups["v" + n].Value;
                    return nested[n] ? Line(value, depth + 1) : value;
                });
                return parts.Groups[1].Value + done + parts.Groups[3].Value;
            }
            return line;
        }

        // ------------------------------------------------------------------ dialogs

        public static MessageBoxResult Show(string text, string caption = "CoRFAGen",
            MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
            System.Windows.MessageBox.Show(T(text), T(caption), button, image);

        public static Autodesk.Revit.UI.TaskDialogResult ShowTask(string title, string text) =>
            RevitDialog.Show(T(title), T(text));

        public static RevitDialog Localize(RevitDialog dialog)
        {
            dialog.Title = T(dialog.Title);
            dialog.MainInstruction = T(dialog.MainInstruction);
            dialog.MainContent = T(dialog.MainContent);
            dialog.FooterText = T(dialog.FooterText);
            return dialog;
        }

        public static void AddLink(RevitDialog dialog, Autodesk.Revit.UI.TaskDialogCommandLinkId id, string main, string description = null)
        {
            if (description == null) dialog.AddCommandLink(id, T(main));
            else dialog.AddCommandLink(id, T(main), T(description));
        }

        // ------------------------------------------------------------------ windows

        /// <summary>English originals, so a window can be switched back without being reopened.</summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, string>> Originals =
            new ConditionalWeakTable<object, Dictionary<string, string>>();

        private static string Tr(object element, string property, string current)
        {
            Dictionary<string, string> saved = Originals.GetOrCreateValue(element);
            if (!saved.TryGetValue(property, out string english))
                saved[property] = english = current;
            return T(english);
        }

        /// <summary>Translates the fixed texts of a window, including button labels and tooltips.</summary>
        public static void Apply(DependencyObject root)
        {
            if (root == null) return;
            if (IsRussian) EnsureLoaded();

            if (root is Window w && !string.IsNullOrEmpty(w.Title)) w.Title = Tr(w, "Title", w.Title);

            if (root is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = Tr(fe, "ToolTip", tip);

            switch (root)
            {
                case C.TextBlock tb when tb.Inlines.Count <= 1 && !BindingOperations.IsDataBound(tb, C.TextBlock.TextProperty) && !string.IsNullOrEmpty(tb.Text):
                    tb.Text = Tr(tb, "Text", tb.Text);
                    break;
                case C.TextBox box when !BindingOperations.IsDataBound(box, C.TextBox.TextProperty) && !string.IsNullOrEmpty(box.Text)
                                      && (IsRussian ? Exact.ContainsKey(box.Text) : HasOriginal(box)):
                    box.Text = Tr(box, "Text", box.Text);
                    break;
                case C.Primitives.ButtonBase b when b.Content is string s:   // Button, CheckBox, RadioButton
                    b.Content = Tr(b, "Content", s);
                    break;
                case C.Label l when l.Content is string s:
                    l.Content = Tr(l, "Content", s);
                    break;
                case C.ComboBoxItem i when i.Content is string s:
                    i.Content = Tr(i, "Content", s);
                    break;
                case C.HeaderedContentControl h when h.Header is string s:
                    h.Header = Tr(h, "Header", s);
                    break;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject d) Apply(d);
        }

        private static bool HasOriginal(object element) =>
            Originals.TryGetValue(element, out Dictionary<string, string> saved) && saved.ContainsKey("Text");
    }
}
