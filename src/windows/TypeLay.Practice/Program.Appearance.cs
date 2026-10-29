using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Practice;

internal static partial class Program
{
    private static UserSettings _preferences = new();
    private static nint _contentFont, _cardBrush;
    private static uint _cardColor, _mutedColor, _accentColor;
    private static Bounds _inputCard, _coachCard;
    private static readonly List<nint> PrivateFonts = [];
    private static bool English => _preferences.UiLanguage == "en" || _preferences.UiLanguage == "system"
        && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    private static string L(string chinese, string english) => English ? english : chinese;

    private static void LoadPrivateFonts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".ttf", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null || stream.Length > 2 * 1024 * 1024)
                continue;
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            nint data = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, data, bytes.Length);
                uint count = 0;
                nint font = AddFontMemResourceEx(data, (uint)bytes.Length, 0, ref count);
                if (font != 0)
                    PrivateFonts.Add(font);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }

    private static void ReleaseAppearance()
    {
        if (_contentFont != 0)
            Native.DeleteObject(_contentFont);
        if (_cardBrush != 0)
            Native.DeleteObject(_cardBrush);
        foreach (nint font in PrivateFonts)
            RemoveFontMemResourceEx(font);
        PrivateFonts.Clear();
    }

    private static string UiCopy(string value)
    {
        if (!English || value.Length == 0)
            return value;
        return value switch
        {
            "完整翻译练习" => "Automatic Translation Practice",
            "按需翻译练习" => "On-Demand Translation Practice",
            "练习输入框" => "Practice text field",
            "关闭练习" => "Close Practice",
            "重新开始练习" => "Restart Practice",
            "完成练习" => "Finish Practice",
            "正在保存…" => "Saving…",
            "重试保存" => "Retry Save",
            SettingsUnavailable => "Couldn’t read current settings. Check TypeLay Settings before practising.",
            "TypeLay 已暂停，请在常规设置启用后练习。" => "TypeLay is paused. Enable it in General Settings to practise.",
            "源语言和目标语言相同，请在语言设置选择不同的目标语言后练习。" => "Source and target are the same. Choose a different target language to practise.",
            "当前为按需模式。开始完整练习前，需要确认切换为自动模式。" => "Currently in on-demand mode. Confirm a switch to automatic mode before this practice.",
            "当前为自动模式。开始按需练习前，需要确认切换工作模式。" => "Currently in automatic mode. Confirm a switch before on-demand practice.",
            "开始练习后，这里会显示进度。" => "Your progress will appear here after practice starts.",
            "请点击练习输入框，等待原生连接确认。" => "Click the practice field and wait for its connection to be verified.",
            "完整练习已完成：已验证自动接受和编辑后接受。" => "Practice complete: automatic and edited acceptance were both verified.",
            "请实际修改浮层中的译文。" => "Make a change to the translation in the overlay.",
            "已验证第一次接受。请在原输入框输入新的原文，等待译文后进入编辑。" => "First acceptance verified. Type new source text, then click its translation to edit.",
            "已识别练习输入框，等待自动生成预览。" => "Practice field identified. Waiting for a translation preview.",
            "练习输入框尚未连接，请点击输入框；若仍未连接，请检查应用支持。" => "Practice field not connected. Click it; if it stays disconnected, check App Support.",
            "按需练习已完成：已生成预览，并已验证直接替换。" => "On-demand practice complete: preview and direct replacement were verified.",
            "已生成预览。请回到练习输入框，等待直接替换快捷键就绪。" => "Preview generated. Return to the practice field and wait for direct replacement to be ready.",
            "已生成预览。请回到练习输入框，完成第 2 步直接替换。" => "Preview generated. Return to the practice field for direct replacement.",
            "已识别练习输入框，正在观察；请等待预览快捷键就绪。" => "Practice field identified. Waiting for the preview shortcut to be ready.",
            "已识别练习输入框，尚未生成预览。" => "Practice field identified. No preview has been generated yet.",
            "练习已完成，但未能保存。请重试保存，无需重新练习。" => "Practice is complete, but saving failed. Retry saving; no need to repeat the practice.",
            "本次完成状态已失效，未保存。请重新开始练习。" => "This completion is no longer valid and was not saved. Restart the practice.",
            _ when value == $"已回到原输入框，原文未变。请再按 {_preferences.AcceptShortcut} 接受修改后的译文。" => $"Back at the source, unchanged. Press {_preferences.AcceptShortcut} again to accept your edited translation.",
            _ when value == $"已修改译文。请按 {_preferences.AcceptShortcut} 回到原输入框，此次不会写入。" => $"Translation edited. Press {_preferences.AcceptShortcut} to return; this press will not replace anything.",
            _ when value == $"已生成预览。请在练习输入框按 {_preferences.AcceptShortcut} 接受。" => $"Preview generated. Press {_preferences.AcceptShortcut} in the practice field to accept.",
            _ when value == $"已生成预览。直接替换快捷键已就绪，可按 {_preferences.DirectShortcut} 完成第 2 步。" => $"Preview generated. Direct replacement is ready: press {_preferences.DirectShortcut} for step 2.",
            _ when value == $"预览快捷键已就绪，可按 {_preferences.WakeShortcut} 查看译文。" => $"Preview shortcut ready: press {_preferences.WakeShortcut} to see the translation.",
            _ => value
        };
    }

    private static bool RefreshPracticeLabels()
    {
        bool automatic = _automatic;
        string first = automatic ? L("① 自动翻译并接受", "① Translate and accept") : L("① 唤醒翻译", "① Wake translation");
        string second = automatic ? L("② 编辑、返回并接受", "② Edit, return and accept") : L("② 直接替换", "② Replace in place");
        bool firstDone = automatic ? _automaticProgress?.AcceptanceCompleted == true : _progress?.PreviewCompleted == true;
        bool secondDone = automatic ? _automaticProgress?.EditedAcceptanceCompleted == true : _progress?.DirectReplacementCompleted == true;
        return SetText(_summary!, L("输入内容只在本机处理，不会发送。", "Your text stays on this computer. Nothing is sent."))
            | SetText(_preview!, (firstDone ? "✓ " : "") + first)
            | SetText(_replace!, (secondDone ? "✓ " : "") + second)
            | SetText(_inputLabel!, "练习输入框")
            | SetText(_restart!, "重新开始练习")
            | SetText(_close!, "关闭练习");
    }

    private static void PaintPracticeCards(nint dc)
    {
        if (_systemColors || _cardBrush == 0)
            return;
        void Card(Bounds bounds, bool focus)
        {
            if (bounds.Height <= 0)
                return;
            nint pen = CreatePen(0, Scale(1), focus ? _accentColor : _dark ? 0x003E4142u : 0x00E2E7E8u);
            if (pen == 0)
                return;
            nint oldPen = Native.SelectObject(dc, pen), oldBrush = Native.SelectObject(dc, _cardBrush);
            try
            {
                RoundRect(dc, bounds.X, bounds.Y - _scroll, bounds.X + bounds.Width, bounds.Y - _scroll + bounds.Height, Scale(24), Scale(24));
            }
            finally
            {
                Native.SelectObject(dc, oldBrush);
                Native.SelectObject(dc, oldPen);
                Native.DeleteObject(pen);
            }
        }
        Card(_coachCard, false);
        Card(_inputCard, _edit is not null && Native.GetFocus() == _edit.Handle);
    }

    [DllImport("gdi32.dll")] private static extern nint AddFontMemResourceEx(nint data, uint size, nint reserved, ref uint fonts);
    [DllImport("gdi32.dll")] private static extern bool RemoveFontMemResourceEx(nint font);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
}
