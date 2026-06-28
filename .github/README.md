# TTSMixmaster

A comprehensive tool for exporting your favorite playlists and collections to Tabletop Simulator playlist chips, designed for use with the [Music Box](https://steamcommunity.com/sharedfiles/filedetails/?id=3114355269). This application allows you to fetch playlists and collections from multiple music services including Last.fm, YouTube, and Spotify, download MP3 files, upload them to Azure Blob Storage, and generate formatted Lua code for use in Tabletop Simulator.

## 🌟 Major Update: Multi-Service Integration

TTSMixmaster has been completely overhauled with multi-service support! The application now features:

- **Unified Interface**: Single "Collections & Playlists" tab supporting Last.fm, YouTube, and Spotify
- **Service Abstraction**: Clean architecture separating different music services
- **Enhanced Configuration**: Individual API credentials and connection testing for each service
- **Terminology Accuracy**: Properly distinguishes Last.fm "collections" from true playlists
- **Flexible Usage**: Use only the services you need - all API credentials are optional

![Application Screenshot](./Screenshot%202025-06-22%20141024.png)

## Features

### 🎵 Multi-Service Integration
- **Last.fm Collections**: Fetch user's top tracks, loved tracks, and recent tracks 
  - *Note: Last.fm provides "collections" rather than true playlists via their API*
  - Support for different time periods (overall, 7 days, 1 month, 3 months, 6 months, 12 months)
  - Configurable track limits and filtering
- **YouTube Playlists**: Complete YouTube Data API v3 integration
  - Access user playlists, liked videos, and watch later lists
  - Search and browse public playlists
  - Channel-specific playlist management
- **Spotify Playlists**: Full Spotify Web API integration
  - Browse user's personal playlists
  - Search and discover public playlists
  - User-specific playlist access with optional authentication
- **Unified Interface**: Single "Collections & Playlists" tab supporting all services seamlessly
- **Service Manager**: Centralized configuration and management
- **Connection Testing**: Individual test buttons for each service with real-time validation
- **Flexible Configuration**: Use only the services you need - all credentials are optional

### ⬇️ Audio Downloading
- Download audio files from YouTube and SoundCloud using yt-dlp
- Configurable audio quality (128, 192, 256, 320 kbps)
- Batch downloading with real-time progress tracking
- Automatic file naming and organization
- Support for multiple search engines
- Robust error handling and retry mechanisms

### ☁️ Cloud Upload Support
- **Azure Blob Storage**: Upload to Azure with automatic public URL generation
- **Batch Upload Processing**: Handle multiple files efficiently
- **Real-time Progress Tracking**: Monitor upload status and completion
- **Container Management**: Automatic container creation and management
- **Error Recovery**: Robust error handling with detailed feedback

### 🎮 Tabletop Simulator Integration
- Generate Lua scripts for TTS music players with advanced controls
- Create JSON data structures for external integrations
- Generate complete TTS save files with **custom image support**
- Support for playlist management and advanced playback controls
- **Custom Image URLs**: Set primary and secondary images for TTS objects
- **Simple and Advanced Formats**: Choose between simple playlist format and full music player format
- **Object Customization**: Custom nicknames, descriptions, and visual elements

### 🖥️ Modern GUI Interface
- Built with CustomTkinter for modern, responsive appearance
- Dark/light theme support with system integration
- Real-time progress tracking for all operations
- Tabbed interface for organized, intuitive workflow
- Configuration management with validation and testing
- **Multi-Service Configuration**: Dedicated configuration for each music service
- **Visual Feedback**: Connection status indicators and real-time updates
- **Image URL Management**: Dedicated fields for TTS object customization

## Installation

### Quick Setup
```bash
# Clone and setup
git clone <repository-url>
cd TTSMixmaster
python -m venv venv
venv\Scripts\activate  # Windows

# Install dependencies
pip install .

# Configure (optional)
cp .env.template .env
# Edit .env with your API credentials

# Run
python main.py
```

📖 **[Full Installation Guide](../docs/INSTALLATION.md)** - Detailed setup instructions, API configuration, and troubleshooting

## Usage

### Basic Workflow
1. **Configure**: Add API credentials in the Configuration tab (optional)
2. **Test**: Use connection test buttons to verify service access
3. **Browse**: Switch to "Collections & Playlists" tab and select a service
4. **Load**: Choose a collection/playlist and load tracks
5. **Download**: Go to Download tab and start audio downloading
6. **Upload**: (Optional) Upload to Azure for cloud URLs
7. **Generate**: Create TTS code in the TTS Format tab

📖 **[Detailed Usage Guide](../docs/USAGE.md)** - Complete walkthrough of all features and workflows

## Documentation

- 📖 **[Installation Guide](../docs/INSTALLATION.md)** - Complete setup instructions and API configuration
- 📖 **[Usage Guide](../docs/USAGE.md)** - Detailed walkthrough of all features and workflows  
- 📖 **[Configuration Reference](../docs/CONFIGURATION.md)** - Environment variables and settings
- � **[API Reference](../docs/API_REFERENCE.md)** - Service integrations and authentication
- 📖 **[TTS Integration](../docs/TTS_INTEGRATION.md)** - Tabletop Simulator usage and examples
- 📖 **[Troubleshooting](../docs/TROUBLESHOOTING.md)** - Common issues and solutions

## Contributing

We welcome contributions! Please see our **[Contributing Guide](CONTRIBUTING.md)** for detailed information on:

- Development setup and workflow
- Coding guidelines and best practices  
- Testing requirements
- Pull request process
- Areas where we need help

## License

This project is provided as-is for educational and personal use. Please respect the terms of service of all integrated APIs and services.

## Disclaimer

- This tool is for personal use with your own music data
- Respect copyright and licensing when downloading audio  
- When using custom images, ensure you have the right to use them
- The developers are not responsible for any misuse of this software
