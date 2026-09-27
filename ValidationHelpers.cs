using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace NewsWatch;

public static class ValidationHelpers
{
    /// <summary>
    /// Restricts a TextBox to digits only: blocks typed non-digits (including
    /// '-'), the space key, and pastes containing anything but digits.
    /// </summary>
    public static void MakeNumeric(TextBox box)
    {
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        box.PreviewKeyDown += (_, e) => e.Handled = e.Key == Key.Space;
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            var text = e.DataObject.GetData(DataFormats.Text) as string;
            if (text == null || !text.All(char.IsAsciiDigit))
                e.CancelCommand();
        });
    }

    /// <summary>Opens the native colour picker seeded with the given hex; returns "#RRGGBB" or null on cancel.</summary>
    public static string? PickColor(string currentHex)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (ParseColor(currentHex) is { } seed)
            dialog.Color = System.Drawing.Color.FromArgb(seed.R, seed.G, seed.B);
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"
            : null;
    }

    /// <summary>Parses "#RRGGBB" (or any WPF colour string); null when invalid.</summary>
    public static System.Windows.Media.Color? ParseColor(string text)
    {
        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(text.Trim())
                is System.Windows.Media.Color c)
                return c;
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>
    /// Normalizes a feed URL: trims, adds https:// when no scheme was typed,
    /// maps feed:// and feed:https:// to http(s). Returns null unless the
    /// result is an absolute http(s) URL with a host.
    /// </summary>
    public static string? NormalizeFeedUrl(string? input)
    {
        var url = (input ?? "").Trim();
        if (url.Length == 0 || url.Any(char.IsWhiteSpace)) return null;

        if (url.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
        {
            url = url[5..];
            if (url.StartsWith("//")) url = "http:" + url;
        }
        if (!url.Contains("://")) url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        return uri.AbsoluteUri;
    }

    /// <summary>Opens an http(s) link in the default browser; anything else is ignored.</summary>
    public static void OpenInBrowser(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // no default browser registered
        }
    }
}
