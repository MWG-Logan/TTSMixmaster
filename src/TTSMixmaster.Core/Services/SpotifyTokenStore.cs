using System.Security.Cryptography;
using System.Text.Json;
using TTSMixmaster.Core.Configuration;

namespace TTSMixmaster.Core.Services;

/// <summary>Stores only the refresh token, encrypted for the current Windows user under AppPaths.DataRoot.</summary>
public sealed class SpotifyTokenStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.DataRoot, "spotify_token.json");

    public async Task<string?> LoadAsync(string clientId, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new ServiceException("Spotify token storage requires Windows.");
        try
        {
            if (!File.Exists(FilePath)) return null;
            var encrypted = await File.ReadAllBytesAsync(FilePath, ct);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                using var json = JsonDocument.Parse(bytes);
                return json.RootElement.Text("clientId") == clientId ? json.RootElement.Text("refreshToken") : null;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { throw new ServiceException("The saved Spotify sign-in could not be read. Remove spotify_token.json from the app data folder and sign in again."); }
    }

    public async Task SaveAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new ServiceException("Spotify token storage requires Windows.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { clientId, refreshToken });
        string? temp = null;
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(paths.DataRoot);
            temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, encrypted, ct);
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new ServiceException("Spotify sign-in could not be saved securely. Check access to the app data folder."); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Clear()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ServiceException("The saved Spotify sign-in could not be removed. Check access to the app data folder."); }
    }
}
