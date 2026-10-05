using System.Runtime.CompilerServices;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium;

/// <summary>
/// Translates runtime text through the plugin's strings.&lt;code&gt;.json files.
/// <see cref="IPluginHost.Tr"/> exists from SDK 1.24 on; on an older 1.x host
/// the call fails and the English text is shown instead.
/// </summary>
internal static class PluginText
{
    public static string Tr(IPluginHost? host, string english)
    {
        if (host == null) return english;

        try
        {
            return CallTr(host, english);
        }
        catch (MissingMethodException)
        {
            return english;
        }
    }

    public static string Format(IPluginHost? host, string englishFormat, params object?[] args) =>
        string.Format(Tr(host, englishFormat), args);

    // Kept out of line so a missing member fails here, inside the caller's try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CallTr(IPluginHost host, string english) => host.Tr(english);
}
