using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Holds the OAuth tokens for Spotify. Persisted to <see cref="IPluginSettings"/>
/// as an AES-encrypted JSON blob. The encryption key is derived from
/// <c>MachineName + UserName</c> via PBKDF2, so a user copying settings.json to
/// another machine cannot decrypt the refresh token. Cross-platform — does not
/// depend on DPAPI.
/// </summary>
public sealed class TokenStore
{
    private const string SettingsKey = "spotify_token_v1";
    private const int Iterations = 100_000;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("LoupixDeck.SpotifyPremium.v1");

    private readonly IPluginSettings _settings;
    private TokenData? _cached;

    public TokenStore(IPluginSettings settings)
    {
        _settings = settings;
    }

    public bool HasToken => Load() != null;

    public TokenData? Load()
    {
        if (_cached != null) return _cached;

        var blob = _settings.Get<string>(SettingsKey);
        if (string.IsNullOrEmpty(blob)) return null;

        try
        {
            var json = Decrypt(blob);
            _cached = JsonSerializer.Deserialize<TokenData>(json);
            return _cached;
        }
        catch
        {
            return null;
        }
    }

    public void Save(TokenData data)
    {
        _cached = data;
        var json = JsonSerializer.Serialize(data);
        _settings.Set(SettingsKey, Encrypt(json));
        _settings.Save();
    }

    public void Clear()
    {
        _cached = null;
        _settings.Set<string>(SettingsKey, null);
        _settings.Save();
    }

    private static byte[] DeriveKey()
    {
        var material = Encoding.UTF8.GetBytes(Environment.MachineName + "\0" + Environment.UserName);
        using var pbkdf2 = new Rfc2898DeriveBytes(material, Salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Bundle IV + ciphertext, base64 the whole thing.
        var combined = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, combined, aes.IV.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private static string Decrypt(string blob)
    {
        var combined = Convert.FromBase64String(blob);
        using var aes = Aes.Create();
        aes.Key = DeriveKey();

        var iv = new byte[16];
        Buffer.BlockCopy(combined, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var cipher = combined.Skip(iv.Length).ToArray();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }
}

public sealed class TokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public string Scope { get; set; } = string.Empty;
}
