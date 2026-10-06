using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RepoBackup.Core.Models;

namespace RepoBackup.Desktop.Infrastructure;

public sealed class OutcomeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value switch { Outcome.Successful => "#79D994", Outcome.Failed => "#F08088", Outcome.Incomplete or Outcome.Interrupted => "#FFC361", Outcome.Running => "#65ADFF", _ => "#9BAABF" }));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
public sealed class DisplayLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Coverage.FullProject => "Full project", Coverage.PartialSelection => "Partial selection", Outcome.NotRun => "Not run",
        DestinationProtection.PasswordFree => "No password required", DestinationProtection.RecoveryKey => "Protect with a recovery key",
        DateTimeOffset time => time.LocalDateTime.ToString("MMM d, h:mm tt", culture), _ => value?.ToString() ?? "—"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
