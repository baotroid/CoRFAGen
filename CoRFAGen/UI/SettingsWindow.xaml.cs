using System.Windows;
using System.Windows.Controls;

namespace CoRFAGen.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsWindowViewModel _viewModel;
        private bool _showingClaudeKey = false;
        private bool _showingGeminiKey = false;
        private bool _showingQwenKey = false;
        private bool _showingOpenRouterKey = false;

        public SettingsWindow()
        {
            InitializeComponent();
            Loc.Apply(this);
            _viewModel = new SettingsWindowViewModel();
            DataContext = _viewModel;
            _viewModel.LanguageChanged += () =>
            {
                Loc.Apply(this);
                // these labels follow the state of the key fields, not the XAML text
                BtnToggleClaudeKey.Content = Loc.T(_showingClaudeKey ? "Hide" : "Show");
                BtnToggleGeminiKey.Content = Loc.T(_showingGeminiKey ? "Hide" : "Show");
                BtnToggleQwenKey.Content = Loc.T(_showingQwenKey ? "Hide" : "Show");
                BtnToggleOpenRouterKey.Content = Loc.T(_showingOpenRouterKey ? "Hide" : "Show");
            };

            ClaudeKeyPasswordBox.Password = _viewModel.ClaudeApiKey;
            GeminiKeyPasswordBox.Password = _viewModel.GeminiApiKey;
            QwenKeyPasswordBox.Password = _viewModel.QwenApiKey;
            OpenRouterKeyPasswordBox.Password = _viewModel.OpenRouterApiKey;
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void ClaudeKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_showingClaudeKey)
                _viewModel.ClaudeApiKey = ClaudeKeyPasswordBox.Password;
        }

        private void GeminiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_showingGeminiKey)
                _viewModel.GeminiApiKey = GeminiKeyPasswordBox.Password;
        }

        private void QwenKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_showingQwenKey)
                _viewModel.QwenApiKey = QwenKeyPasswordBox.Password;
        }

        private void BtnToggleQwenKey_Click(object sender, RoutedEventArgs e)
        {
            _showingQwenKey = !_showingQwenKey;
            ToggleKey(_showingQwenKey, QwenKeyPasswordBox, QwenKeyTextBox, BtnToggleQwenKey, _viewModel.QwenApiKey);
        }

        private void OpenRouterKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_showingOpenRouterKey)
                _viewModel.OpenRouterApiKey = OpenRouterKeyPasswordBox.Password;
        }

        private void BtnToggleOpenRouterKey_Click(object sender, RoutedEventArgs e)
        {
            _showingOpenRouterKey = !_showingOpenRouterKey;
            ToggleKey(_showingOpenRouterKey, OpenRouterKeyPasswordBox, OpenRouterKeyTextBox, BtnToggleOpenRouterKey, _viewModel.OpenRouterApiKey);
        }

        private void BtnToggleClaudeKey_Click(object sender, RoutedEventArgs e)
        {
            _showingClaudeKey = !_showingClaudeKey;
            ToggleKey(_showingClaudeKey, ClaudeKeyPasswordBox, ClaudeKeyTextBox, BtnToggleClaudeKey, _viewModel.ClaudeApiKey);
        }

        private void BtnToggleGeminiKey_Click(object sender, RoutedEventArgs e)
        {
            _showingGeminiKey = !_showingGeminiKey;
            ToggleKey(_showingGeminiKey, GeminiKeyPasswordBox, GeminiKeyTextBox, BtnToggleGeminiKey, _viewModel.GeminiApiKey);
        }

        private static void ToggleKey(bool show, System.Windows.Controls.PasswordBox passwordBox,
            System.Windows.Controls.TextBox textBox, System.Windows.Controls.Button button, string key)
        {
            if (show)
            {
                textBox.Text = key;
                passwordBox.Visibility = System.Windows.Visibility.Collapsed;
                textBox.Visibility = System.Windows.Visibility.Visible;
                button.Content = Loc.T("Hide");
            }
            else
            {
                passwordBox.Password = key;
                textBox.Visibility = System.Windows.Visibility.Collapsed;
                passwordBox.Visibility = System.Windows.Visibility.Visible;
                button.Content = Loc.T("Show");
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
