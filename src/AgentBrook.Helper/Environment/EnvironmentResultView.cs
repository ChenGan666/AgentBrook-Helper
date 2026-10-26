using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AgentBrook.Helper.EnvironmentCheck;

internal static class EnvironmentResultView
{
    internal static string Summary(IEnumerable<EnvironmentCheckResult> results, bool repaired = false)
    {
        var errors = results.Count(r => r.Status == CheckStatus.Error);
        var warnings = results.Count(r => r.Status == CheckStatus.Warning);
        return errors == 0 && warnings == 0
            ? I18n.T(repaired ? "ui.all.passed.after.repair" : "ui.all.passed")
            : I18n.T(repaired ? "ui.after.repair.value.error.s.value.warning.s" : "ui.check.complete.value.error.s.value.warning.s", errors, warnings);
    }

    internal static void Render(StackPanel list, IEnumerable<EnvironmentCheckResult> results,
        Func<EnvironmentCheckResult, Task> repair, Func<EnvironmentCheckResult, Task>? repairOther = null, bool busy = false)
    {
        list.Children.Clear();
        foreach (var result in results)
        {
            var color = result.Status switch
            {
                CheckStatus.Ok => "#4cc38a",
                CheckStatus.Warning => "#e0a030",
                CheckStatus.Error => "#ff6b6b",
                _ => "#8a8a8a",
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = Brush.Parse(color), Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            var content = new StackPanel { Spacing = 2 };
            Grid.SetColumn(content, 1);
            content.Children.Add(new TextBlock
            {
                Text = result.Title, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#f0f0f0"), TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = result.Message, FontSize = 12, Foreground = Brush.Parse("#b0b0b0"),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(result.Solution))
                content.Children.Add(new TextBlock
                {
                    Text = I18n.T("ui.solution.value", result.Solution), FontSize = 11.5,
                    Foreground = Brush.Parse("#d29922"), TextWrapping = TextWrapping.Wrap,
                });
            grid.Children.Add(content);
            if (result.CanRepair || repairOther is not null && result.Status == CheckStatus.Error &&
                (result.Id.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase) || result.Id == "CommandExecution"))
            {
                var button = new Button
                {
                    Classes = { "ghostBtn" }, Content = I18n.T("ui.repair"), IsEnabled = !busy,
                    Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                button.Click += async (_, _) => await (result.CanRepair ? repair(result) : repairOther!(result));
                Grid.SetColumn(button, 2);
                grid.Children.Add(button);
            }
            list.Children.Add(grid);
            list.Children.Add(new Border { Height = 1, Background = Brush.Parse("#333333"), Margin = new Thickness(0, 6, 0, 0) });
        }
    }
}
