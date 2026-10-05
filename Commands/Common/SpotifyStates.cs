using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Common;

/// <summary>
/// Button states the stateful commands declare (SDK 1.21); the host creates them when the command
/// is assigned and the plugin switches between them with <see cref="IPluginHost.SetActiveButtonState"/>.
/// Each state is drawn by the command itself: an MDI symbol with a caption below it, in the same
/// geometry as <see cref="SpotifyIcons.IconWithCaption"/>.
/// </summary>
/// <remarks>The state names are persisted in button bindings — never rename them. The first one is the resting state.</remarks>
internal static class SpotifyStates
{
    public const string Paused = "Paused";
    public const string Playing = "Playing";
    public const string Unmuted = "Unmuted";
    public const string Muted = "Muted";

    public static readonly PluginColor Normal = PluginColor.White;
    public static readonly PluginColor Green = PluginColor.FromRgb(0x1E, 0xD7, 0x60);
    public static readonly PluginColor Red = PluginColor.FromRgb(0xED, 0x42, 0x45);

    public static IReadOnlyList<ButtonStateDescriptor> Playback { get; } =
    [
        new() { Name = Paused, Description = "Playback is paused" },
        new() { Name = Playing, Description = "Spotify is playing" }
    ];

    public static IReadOnlyList<ButtonStateDescriptor> Mute { get; } =
    [
        new() { Name = Unmuted, Description = "Spotify is audible" },
        new() { Name = Muted, Description = "Spotify volume is 0" }
    ];

    /// <summary>
    /// The state to draw. Follows the live Spotify state rather than the button's active state, so a
    /// freshly assigned button (which starts in the resting state) never shows the wrong picture; a
    /// mismatch is corrected in the background so the user's own per-state layers follow too.
    /// </summary>
    public static string Resolve(CommandContext ctx, string commandName, string live)
    {
        if (ctx.StateName != null && !string.Equals(ctx.StateName, live, StringComparison.OrdinalIgnoreCase))
            _ = Task.Run(() => Push(ctx.Host, commandName, live));

        return live;
    }

    /// <summary>Shows <paramref name="state"/> on every button bound to <paramref name="commandName"/>.</summary>
    public static void Push(IPluginHost host, string commandName, string state)
    {
        try
        {
            host.SetActiveButtonState(commandName, state);
        }
        catch (Exception ex)
        {
            // The host may not be ready yet, or the button was unbound meanwhile.
            host.Logger.Warn($"Could not update button state for {commandName}: {ex.Message}");
        }
    }

    /// <summary>Draws <paramref name="symbol"/> (an MDI name) with <paramref name="caption"/> below it.</summary>
    public static bool Draw(IRenderCanvas canvas, string symbol, string caption, PluginColor color)
    {
        // Same geometry as SpotifyIcons.IconWithCaption, scaled from a 90 px key, so a drawn state
        // looks like the layout the other commands create.
        double scale = Math.Min(canvas.Width, canvas.Height) / 90.0;
        int iconSize = (int)Math.Round(90 * SpotifyIcons.IconScale * scale);
        int iconX = (canvas.Width - iconSize) / 2;
        int iconY = ((canvas.Height - iconSize) / 2) + (int)Math.Round(SpotifyIcons.IconOffsetY * scale);
        canvas.DrawSymbol(symbol, iconX, iconY, iconSize, iconSize, color);

        int boxWidth = (int)Math.Round(SpotifyIcons.CaptionBoxWidth * scale);
        int boxHeight = (int)Math.Round(SpotifyIcons.CaptionBoxHeight * scale);
        int boxY = ((canvas.Height - boxHeight) / 2) + (int)Math.Round(SpotifyIcons.CaptionOffsetY * scale);
        canvas.DrawText(caption, (canvas.Width - boxWidth) / 2, boxY, boxWidth, boxHeight,
            color, (float)(SpotifyIcons.CaptionSize * scale));

        return true;
    }
}
