# Troubleshooting Guide

## Common Setup Issues

### Python Virtual Environment Issues
```bash
# If virtual environment creation fails
python -m pip install --upgrade pip
python -m pip install virtualenv
python -m virtualenv venv
```

### Dependency Installation Problems
```bash
# If pip install fails, try upgrading pip first
python -m pip install --upgrade pip

# Install dependencies one by one if batch install fails
pip install customtkinter
pip install yt-dlp
pip install requests
pip install python-dotenv
```

### FFmpeg Error (`'FFmpegPostProcessorPP'`)
- The `imageio-ffmpeg` package should handle FFmpeg automatically
- If issues persist, install FFmpeg manually from https://ffmpeg.org/

### Import Errors
- Ensure you're using the correct virtual environment
- Verify all dependencies are installed: `pip list`
- Try running from the project root directory

## API Connection Issues

### Last.fm Connection Failed
- Verify your API credentials in the `.env` file
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
- Verify internet connection and try different search engines
- Some videos may be region-restricted or unavailable
- Update yt-dlp if outdated: `pip install --upgrade yt-dlp`
- Check for age-restricted or private content
- Verify the search terms are finding valid results

### Upload Issues
- **For Azure**: Verify connection string and container permissions
- Check network connectivity and firewall settings
- Ensure sufficient storage account space
- Verify file permissions and sizes

### TTS Generation Errors
- Ensure you have valid playlist data before generating TTS code
- Check that output paths are writable
- Verify custom image URLs are valid and publicly accessible (if used)
- Ensure sufficient disk space for output files

### GUI Not Starting
- Ensure CustomTkinter is properly installed: `pip install customtkinter`
- Try running with: `python -m src.gui.main_window` from project root
- Check for conflicting tkinter installations
- Verify Python version compatibility (3.8+)

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
1. Check the `ttsmixmaster.log` file for detailed error messages
2. Run basic functionality test: `python tests/test_basic.py`
3. Test multi-service integration: `python tests/test_multi_service.py`
4. Verify configuration using test buttons in the Configuration tab

### Configuration Validation
Use the built-in test buttons in the Configuration tab to validate:
- ✅ Last.fm API credentials and username
- ✅ YouTube Data API v3 key and permissions
- ✅ Spotify Client ID and Secret
- ✅ Azure Storage connection and container access

These tests provide immediate feedback on configuration issues.

### Log Files
Check the following log files for detailed error information:
- `ttsmixmaster.log` - Main application log
- Console output - Real-time error messages
- Service-specific error messages in the GUI

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

### Environment Issues
- Verify `.env` file formatting
- Check for hidden characters or encoding issues
- Ensure environment variables are properly loaded

### Network Issues
- Check firewall settings
- Verify proxy configurations if applicable
- Test with different network connections

### System Issues
- Ensure sufficient system resources (RAM, disk space)
- Check for conflicting software
- Verify Python installation integrity

### Database/Cache Issues
- Clear application cache if problems persist
- Reset configuration to defaults if necessary
- Remove and recreate virtual environment

## When to Seek Additional Help

If issues persist after following this guide:
1. Gather error messages and log files
2. Document the steps that led to the issue
3. Note your system configuration (OS, Python version, etc.)
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
