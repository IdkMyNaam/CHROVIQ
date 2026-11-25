using System.Windows.Controls;

namespace WindowsCustomizer;

/// <summary>
/// Works around a WPF problem with animated controls (WPF-UI's dropdowns, checkboxes, toggles): taking such a
/// control out of the window while its animation runs makes WPF throw an ArgumentNullException deep inside
/// DoubleAnimationBase.GetCurrentValue. It's harmless (the control is being removed anyway).
/// </summary>
internal static class SafeUi
{
    /// <summary>Empties a panel; a child whose animation trips WPF is still removed (the rest are cleared after it).</summary>
    public static void Clear(Panel panel)
    {
        for (int attempt = 0; attempt < 5 && panel.Children.Count > 0; attempt++)
        {
            try
            {
                panel.Children.Clear();
                return;
            }
            catch (ArgumentNullException ex) when (IsAnimationGlitch(ex))
            {
                AppTrace.Log("Cleared a panel while a control was animating (WPF glitch, harmless)");
            }
        }
    }

    /// <summary>The known, harmless WPF animation error (and nothing else).</summary>
    public static bool IsAnimationGlitch(Exception ex) =>
        ex is ArgumentNullException && (ex.StackTrace?.Contains("DoubleAnimationBase.GetCurrentValue", StringComparison.Ordinal) ?? false);
}
