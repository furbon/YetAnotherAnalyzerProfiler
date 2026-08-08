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

        if (string.IsNullOrWhiteSpace(configuration))
        {
            return new MeasurementStatePresentation(false, "ビルド構成を選択してください。");
        }

        bool detected = availableConfigurations.Any(available =>
            available.Equals(configuration, StringComparison.OrdinalIgnoreCase));
        return detected
            ? new MeasurementStatePresentation(true, $"測定可能: {configuration} 構成")
            : new MeasurementStatePresentation(
                true,
                $"測定可能: {configuration} 構成（対象から未検出。入力名でビルドします）");
    }
}
