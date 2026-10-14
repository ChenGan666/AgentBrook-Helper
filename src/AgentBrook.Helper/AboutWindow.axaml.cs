using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AgentBrook.Helper;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ApplyWindowLanguage();
        I18n.LanguageChanged += ApplyWindowLanguage;
        Closed += (_, _) => I18n.LanguageChanged -= ApplyWindowLanguage;
    }

    private void ApplyWindowLanguage()
    {
        Title = I18n.T("about.title");
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0.0";
        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText is not null) versionText.Text = I18n.T("about.version", version);
        var description = this.FindControl<TextBlock>("DescriptionText");
        if (description is not null) description.Text = I18n.T("about.description");
        var learnMore = this.FindControl<Button>("LearnMoreButton");
        if (learnMore is not null) learnMore.Content = I18n.T("about.learn.more");
        var close = this.FindControl<Button>("CloseButton");
        if (close is not null) close.Content = I18n.T("about.close");
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnLearnMoreClick(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://agentbrook.com",
            UseShellExecute = true,
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
