# Troubleshooting Guide

## Installation Issues

### Opening `TTSMixmaster.appinstaller` does nothing
- App Installer may be missing or out of date: install or update
  [App Installer from the Microsoft Store](https://apps.microsoft.com/detail/9NBLGGH4NNS1)
- Organization-managed PCs can block sideloaded apps; ask your IT admin, or use the portable zip

### "This app package isn't signed with a trusted certificate" / publisher can't be verified
- Download the files only from the project's GitHub releases page; official builds are signed by
  Morningwood Gaming LLC
- A file that was modified or only partly downloaded fails this check; download it again

### The portable version is blocked by SmartScreen or antivirus
- Make sure you extracted the whole zip, then run `TTSMixmaster.exe` from the extracted folder
- `TTSMixmaster.exe` is signed by Morningwood Gaming LLC; `yt-dlp.exe` and `ffmpeg.exe` are the
  upstream builds and keep their publishers' signatures (or none)

### Which download do I need?
- Use `TTSMixmaster.appinstaller`: it picks x64 or ARM64 for you and keeps the app updated
- For the portable zip, pick `-arm64` on Windows on Arm (Snapdragon) PCs and `-x64` otherwise

### The app doesn't update
- Only installs made by opening `TTSMixmaster.appinstaller` update themselves; a directly
  opened `.msixbundle`, winget, or a managed (Intune/Configuration Manager) install doesn't
- Updates are checked at most once a day and installed the next time you start the app

## API Connection Issues

### Last.fm Connection Failed
- Verify your API credentials in the app's settings
- Check that your username is correct
- Ensure API key and secret are from https://www.last.fm/api/account/create
- Verify the username exists and is public

### YouTube Connection Failed
- Verify your Data API v3 key in Google Cloud Console
- Ensure the YouTube Data API v3 is enabled in your Google Cloud project
- Check API quotas and usage limits
- Verify the API key has proper permissions

### Spotify Connection Failed
- Confirm your Client ID and Client Secret in the Spotify Developer Dashboard
- **For OAuth Authentication (default)**: 
  - Ensure `http://127.0.0.1:8888/callback` is added to your Redirect URIs in the Spotify app settings
  - For apps in development mode, make sure your Spotify account email is whitelisted in "User Management"
  - If the browser doesn't open automatically, check the console for the authentication URL
  - Tokens are cached in `~/.ttsmixmaster/.spotify_cache` - delete this file if you encounter authentication issues
- **For Client Credentials (public data only)**:
  - Set `spotify_use_oauth` to `false` in your configuration
  - Note: This mode cannot access user playlists or private data
- Check that the redirect URI matches exactly (including protocol and port)
- Verify network connectivity and that no firewall is blocking localhost connections

### Azure Connection Failed
- Verify your connection string format
- Ensure the storage account exists and is accessible
- Check that the container name is valid (lowercase, alphanumeric, hyphens only)
- Verify network connectivity to Azure services

## Runtime Issues

### Download Failures
- Verify your internet connection
- Some videos may be region-restricted or unavailable
- Update TTSMixmaster to the latest release, which bundles a current yt-dlp
- Check for age-restricted or private content
- Verify the search terms are finding valid results

### Upload Issues
- **For Azure**: check the **Storage** section in Settings; it reports missing access or public read
- Check network connectivity and firewall settings
- Ensure sufficient storage account space
- Verify file permissions and sizes

### TTS Generation Errors
- Ensure you have valid playlist data before generating TTS code
- Check that output paths are writable
- Verify custom image URLs are valid and publicly accessible (if used)
- Ensure sufficient disk space for output files

### App Not Starting
- Windows 10 version 1809 or later (or Windows 11) is required
- For the portable version, keep every file from the zip together; `TTSMixmaster.exe` needs the
  files next to it
- Reinstall from the latest release; for an installed copy, **Settings → Apps → Installed apps →
  TTSMixmaster → Advanced options → Repair** often helps

## Performance Issues

### Slow API Responses
- Check internet connection stability
- Some services have rate limits - try reducing concurrent requests
- Large playlists may take time to process
- Consider breaking large requests into smaller batches

### High Memory Usage During Downloads
- Process smaller batches of tracks
- Ensure sufficient disk space in download directory
- Close other memory-intensive applications
- Monitor system resources during operation

### Application Freezing
- Large operations may appear to freeze - check progress indicators
- Ensure the application isn't waiting for user input
- Check log files for error messages
- Restart the application if necessary

## Service-Specific Issues

### Last.fm Issues
- **Empty Results**: Verify username and that the profile is public
- **Rate Limiting**: Last.fm has API rate limits - wait between requests
- **Missing Data**: Some users may have limited public data

### YouTube Issues
- **Quota Exceeded**: YouTube API has daily quotas - wait 24 hours or get more quota
- **Private Videos**: Private or unlisted videos cannot be accessed
- **Geographic Restrictions**: Some content may not be available in your region

### Spotify Issues
- **Authentication Required**: Some features require user authentication
- **Private Playlists**: Private playlists require proper authentication
- **Rate Limiting**: Spotify has rate limits - reduce request frequency

### Azure Issues
- **Storage Limits**: Check storage account limits and quotas
- **Network Issues**: Verify network connectivity to Azure
- **Authentication**: Ensure connection string is valid and has proper permissions

## Data Issues

### Missing Track Information
- Some tracks may have incomplete metadata
- Verify the source playlist/collection has complete information
- Check if tracks are available in the source service

### Download Quality Issues
- Adjust audio quality settings in the configuration
- Some sources may not have high-quality audio available
- Verify yt-dlp is updated to the latest version

### File Organization Problems
- Check file permissions in download and output directories
- Ensure sufficient disk space
- Verify path configurations are correct

## Getting Help

### Diagnostic Steps
1. Check the latest log in `%LOCALAPPDATA%\TTSMixmaster\logs\` (`app-<date>.log`, one per day, the
   last seven kept).
2. Use **Test connection** for each service on the **Settings** page. It checks the Last.fm, YouTube and
   Spotify credentials. The **Storage** section shows whether Azure access and public read are ready.
3. On a collection, each track's status shows why it failed.

### Common Error Messages

#### "API Key Invalid"
- Verify the API key is correctly entered
- Check that the key has the necessary permissions
- Ensure the key hasn't expired

#### "Service Unavailable"
- Check internet connectivity
- Verify the service isn't experiencing downtime
- Try again later if the service is temporarily unavailable

#### "File Not Found"
- Verify file paths are correct
- Check file permissions
- Ensure the file hasn't been moved or deleted

#### "Authentication Failed"
- Verify credentials are correct
- Check that authentication tokens haven't expired
- Ensure proper authentication flow is completed

## Advanced Troubleshooting

### Network Issues
- Check firewall settings
- Verify proxy configurations if applicable
- Test with different network connections

### System Issues
- Ensure sufficient system resources (RAM, disk space)
- Check for conflicting software
- Reinstall TTSMixmaster from the latest release

### Database/Cache Issues
- Clear application cache if problems persist
- Reset configuration to defaults if necessary

## When to Seek Additional Help

If issues persist after following this guide:
1. Gather error messages and log files
2. Document the steps that led to the issue
3. Note your system configuration (Windows version, x64 or ARM64, TTSMixmaster version, how you installed it)
4. Check the project's GitHub issues page for similar problems
5. Consider filing a new issue with detailed information

## Prevention Tips

### Regular Maintenance
- Keep dependencies updated
- Regularly update API keys before expiration
- Monitor service quotas and limits
- Back up configuration files

### Best Practices
- Test configuration changes with small datasets first
- Monitor log files for warnings
- Keep the application updated
- Use version control for configuration files
