using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Auth;

/// <summary>
/// Starts the OAuth flow from a regular button press, mirroring the original
/// Loupedeck plugin's "Login to Spotify" action. The same flow is also reachable
/// from the settings page; this command exists so the user can rebind it.
/// </summary>
internal sealed class LoginCommand : IPluginCommand
{
    private readonly SpotifyPremiumPlugin _plugin;

    public LoginCommand(SpotifyPremiumPlugin plugin) => _plugin = plugin;

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.Login",
        DisplayName = "Connect to Spotify",
        Group = "Spotify Premium",
        Icon = "\U000F075A",
        Description = "Start the Spotify OAuth login",
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        var result = await _plugin.ConnectAsync();
        ctx.Host.Logger.Info($"Login: {result}");
    }
}
