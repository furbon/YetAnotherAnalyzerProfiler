namespace Yaap.Gui;

public sealed record MeasurementStatePresentation(bool CanStart, string Text)
{
    public static MeasurementStatePresentation Create(
        bool isRunning,
        bool isDiscovering,
        bool hasValidTarget,
        string targetPath,
        string configuration,
        IEnumerable<string> availableConfigurations)
    {
        ArgumentNullException.ThrowIfNull(availableConfigurations);

        if (isRunning)
        {
            string runningConfiguration = string.IsNullOrWhiteSpace(configuration)
                ? string.Empty
                : $": {configuration} 構成";
            return new MeasurementStatePresentation(false, $"測定中{runningConfiguration}");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new MeasurementStatePresentation(false, "測定対象を指定してください。");
        }

        if (isDiscovering)
        {
            return new MeasurementStatePresentation(false, "対象とビルド構成を確認中です。");
        }

        if (!hasValidTarget)
        {
            return new MeasurementStatePresentation(false, "測定対象を確認してください。");
        }

        bool hasSelectedConfiguration = !string.IsNullOrWhiteSpace(configuration) &&
            availableConfigurations.Any(available =>
                available.Equals(configuration, StringComparison.OrdinalIgnoreCase));
        if (!hasSelectedConfiguration)
        {
            return new MeasurementStatePresentation(false, "ビルド構成を選択してください。");
        }

        return new MeasurementStatePresentation(true, $"測定可能: {configuration} 構成");
    }
}
