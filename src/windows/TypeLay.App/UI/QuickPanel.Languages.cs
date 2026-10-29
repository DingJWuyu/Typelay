using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TypeLay.App;

internal sealed partial class QuickPanel
{
    private object _languageIntent = new();

    private void InitializeTrayLanguages()
    {
        foreach (var picker in new[] { _sourceLanguage, _targetLanguage })
        {
            picker.SetResourceReference(StyleProperty, "TrayLanguagePicker");
            picker.DropDownOpened += (_, _) =>
            {
                _languageIntent = new();
                var other = ReferenceEquals(picker, _sourceLanguage) ? _targetLanguage : _sourceLanguage;
                other.IsDropDownOpen = false;
            };
        }
        // Reuse the standard popup without admitting additional foreground
        // windows. The existing Deactivated -> Hide boundary is unchanged.
        // A hidden/reopened tray never revives a save.
        IsVisibleChanged += (_, _) =>
        {
            _languageIntent = new();
            if (!IsVisible)
            {
                _sourceLanguage.IsDropDownOpen = false;
                _targetLanguage.IsDropDownOpen = false;
            }
        };
        var glyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M4,8 H19 M15,4 L19,8 L15,12 M20,16 H5 M9,12 L5,16 L9,20"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        glyph.SetResourceReference(Shape.StrokeProperty, "Muted");
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(glyph);
        _swapLanguages.Content = new Viewbox { Width = 14, Height = 14, Child = canvas, IsHitTestVisible = false };
        _swapLanguages.SetResourceReference(StyleProperty, "SettingsLanguageSwap");
        _swapLanguages.Width = _swapLanguages.Height = 30;
        _swapLanguages.HorizontalAlignment = HorizontalAlignment.Center;
        _swapLanguages.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_swapLanguages, "TraySwapLanguages");
    }

    private Task<bool> SaveTrayLanguageAsync(bool source, string code)
    {
        object intent = _languageIntent;
        var picker = source ? _sourceLanguage : _targetLanguage;
        bool ready = _model.Ready;
        string[] directions = _model.AvailableDirections.ToArray();
        bool Current() => IsVisible && ReferenceEquals(intent, _languageIntent)
            && _model.Ready == ready && directions.SequenceEqual(_model.AvailableDirections)
            && picker.CanChoose(code);
        if (!Current())
            return Task.FromResult(false);
        var latest = _settings.Value;
        return SaveAsync(source ? latest with
        {
            Source = code
        } : latest with
        {
            Target = code
        }, Current);
    }

    private Task<bool> SwapTrayLanguagesAsync()
    {
        object intent = _languageIntent = new();
        string source = _settings.Value.Target, target = _settings.Value.Source;
        bool ready = _model.Ready;
        string[] directions = _model.AvailableDirections.ToArray();
        bool Current() => IsVisible && ReferenceEquals(intent, _languageIntent)
            && _model.Ready == ready && directions.SequenceEqual(_model.AvailableDirections)
            && _sourceLanguage.CanChoose(source) && _targetLanguage.CanChoose(target);
        if (target == "auto" || !Current())
            return Task.FromResult(false);
        _sourceLanguage.IsDropDownOpen = false;
        _targetLanguage.IsDropDownOpen = false;
        return SetLanguagesAsync(source, target, Current);
    }
}
