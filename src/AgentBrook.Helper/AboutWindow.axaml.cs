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

        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText is not null)
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0.0";
            versionText.Text = $"版本 {version}";
        }
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
