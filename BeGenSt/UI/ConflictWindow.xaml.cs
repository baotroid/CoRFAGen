using System.Windows.Controls;
using BeGenSt.Data;

namespace BeGenSt.UI
{
    /// <summary>
    /// Shows the contradictions found in the source files (e.g. chain 340+430+90 = 860 vs overall 850)
    /// and lets the user choose the value to use for generation.
    /// </summary>
    public partial class ConflictWindow : Window
    {
        private readonly SourceAnalysis _analysis;
        private readonly List<(SourceConflict conflict, List<System.Windows.Controls.RadioButton> radios,
            System.Windows.Controls.RadioButton customRadio, System.Windows.Controls.TextBox customBox)> _rows =
            new List<(SourceConflict, List<System.Windows.Controls.RadioButton>, System.Windows.Controls.RadioButton, System.Windows.Controls.TextBox)>();

        public List<ConflictDecision> Decisions { get; private set; }

        public ConflictWindow(SourceAnalysis analysis)
        {
            InitializeComponent();
            _analysis = analysis;

            string summary = analysis.Summary();
            if (analysis.TechnicalDataLines.Count > 0)
                summary += "\nTechnical data found: " + string.Join("; ", analysis.TechnicalDataLines.Take(8)) +
                           (analysis.TechnicalDataLines.Count > 8 ? " ..." : "");
            TxtSummary.Text = summary;

            BuildRows();
        }

        private void BuildRows()
        {
            var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
            var surface = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
            var border = (System.Windows.Media.Brush)FindResource("RFAGenBorderBrush");
            var yellow = (System.Windows.Media.Brush)FindResource("YellowBrush");

            int n = 0;
            foreach (SourceConflict c in _analysis.Conflicts)
            {
                n++;
                var card = new Border
                {
                    Background = surface,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 10, 14, 12),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = $"{n}. {c.Question}",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = accent,
                    FontSize = 13
                });
                if (!string.IsNullOrWhiteSpace(c.Affects))
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Affects: " + c.Affects,
                        FontSize = 11,
                        Foreground = yellow,
                        Margin = new Thickness(0, 2, 0, 4),
                        TextWrapping = TextWrapping.Wrap
                    });

                string group = "conflict_" + n;
                var radios = new List<System.Windows.Controls.RadioButton>();
                for (int i = 0; i < c.Options.Count; i++)
                {
                    var rb = new System.Windows.Controls.RadioButton
                    {
                        GroupName = group,
                        Content = new TextBlock
                        {
                            Text = c.Options[i].Label + (i == c.Recommended ? "   (recommended)" : ""),
                            TextWrapping = TextWrapping.Wrap
                        },
                        IsChecked = i == c.Recommended
                    };
                    radios.Add(rb);
                    stack.Children.Add(rb);
                }

                var customPanel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var customRadio = new System.Windows.Controls.RadioButton
                {
                    GroupName = group,
                    Content = "Other value:",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                var customBox = new System.Windows.Controls.TextBox { MinWidth = 160 };
                customBox.GotFocus += (s, e) => customRadio.IsChecked = true;
                DockPanel.SetDock(customRadio, Dock.Left);
                customPanel.Children.Add(customRadio);
                customPanel.Children.Add(customBox);
                stack.Children.Add(customPanel);

                card.Child = stack;
                ConflictPanel.Children.Add(card);
                _rows.Add((c, radios, customRadio, customBox));
            }
        }

        private void BtnRecommended_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
                row.radios[row.conflict.Recommended].IsChecked = true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            var decisions = new List<ConflictDecision>();
            foreach (var row in _rows)
            {
                if (row.customRadio.IsChecked == true)
                {
                    string v = row.customBox.Text?.Trim();
                    if (string.IsNullOrEmpty(v))
                    {
                        System.Windows.MessageBox.Show($"Enter a value for:\n{row.conflict.Question}", "RFAGen",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        row.customBox.Focus();
                        return;
                    }
                    decisions.Add(new ConflictDecision { Id = row.conflict.Id, Question = row.conflict.Question, Answer = v, IsCustom = true });
                    continue;
                }

                int idx = row.radios.FindIndex(r => r.IsChecked == true);
                if (idx < 0) idx = row.conflict.Recommended;
                ConflictOption opt = row.conflict.Options[idx];
                decisions.Add(new ConflictDecision
                {
                    Id = row.conflict.Id,
                    Question = row.conflict.Question,
                    Answer = string.IsNullOrWhiteSpace(opt.Value) ? opt.Label : $"{opt.Value} ({opt.Label})"
                });
            }
            Decisions = decisions;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }
    }
}
