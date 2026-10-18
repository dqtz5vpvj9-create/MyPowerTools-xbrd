using System.Text;

namespace Xbrd.ServiceCommon;

internal static class XbrdServiceLog
{
    /// <summary>Keeps stdout ASCII-only so redirected capture never suffers an encoding mismatch.</summary>
    internal static string AsciiSafe(string text)
    {
        if (text.All(static ch => ch is >= ' ' and <= '~'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch is >= ' ' and <= '~' ? ch : '?');
        }

        return builder.ToString();
    }
}
