# Tabletop Simulator Integration Guide

> **Legacy Python implementation, kept as a reference for the .NET port.** The shipped app is the
> .NET/WinUI app in `src/TTSMixmaster.App` and `src/TTSMixmaster.Core` (see `AGENTS.md`).

## Overview

TTSMixmaster generates Lua scripts and save files that integrate seamlessly with Tabletop Simulator (TTS). This guide covers how to use the generated files, customize TTS objects, and implement music players in your TTS sessions.

## Generated File Types

### 1. Lua Scripts (.lua files)
- **Purpose**: Complete music player objects with controls
- **Usage**: Copy-paste into TTS scripted objects
- **Features**: Play/pause, track navigation, volume control

### 2. JSON Data (.json files)
- **Purpose**: Structured data for external integrations
- **Usage**: Import into other applications or custom scripts
- **Content**: Track metadata, URLs, configuration data

### 3. TTS Save Files (.json save format)
- **Purpose**: Complete TTS save files with embedded music player
- **Usage**: Load directly into TTS as a complete scene
- **Features**: Pre-configured objects with custom images

## Using Generated Files

### Loading Lua Scripts

#### Method 1: Scripted Object
1. Open Tabletop Simulator
2. Create or select an object
3. Right-click → Scripting
4. Paste the generated Lua code
5. Save & Play

#### Method 2: Global Script
1. Go to Scripting → Global
2. Paste the generated code
3. Save & Play

#### Method 3: Load Save File
1. Go to Games → Load
2. Select the generated save file
3. Load the complete scene

### Object Customization

#### Setting Custom Images
When generating TTS code, you can specify:
- **Primary Image URL**: Front/top of the object
- **Secondary Image URL**: Back/bottom of the object

Example URLs:
```
https://example.com/album-cover.jpg
https://imgur.com/playlist-image.png
```

#### Image Requirements
- **Format**: JPG, PNG, GIF
- **Size**: Recommended 512x512 or higher
- **Access**: Must be publicly accessible
- **HTTPS**: Secure URLs preferred

## Music Player Controls

### Basic Controls
```lua
-- Start playback
MusicPlayer.play()

-- Stop playback  
MusicPlayer.stop()

-- Pause/resume
MusicPlayer.pause()

-- Next track
MusicPlayer.next()

-- Previous track
MusicPlayer.previous()
```

### Information Commands
```lua
-- List all tracks
MusicPlayer.listTracks()

-- Show current track info
MusicPlayer.showInfo()

-- Display player status
MusicPlayer.getStatus()

-- Show help
MusicPlayer.help()
```

### Volume Control
```lua
-- Set volume (0.0 to 1.0)
MusicPlayer.setVolume(0.5)

-- Get current volume
local volume = MusicPlayer.getVolume()

-- Mute/unmute
MusicPlayer.mute()
MusicPlayer.unmute()
```

### Playlist Management
```lua
-- Jump to specific track (1-based index)
MusicPlayer.playTrack(5)

-- Shuffle playlist
MusicPlayer.shuffle()

-- Set repeat mode
MusicPlayer.setRepeat(true)

-- Get track count
local count = MusicPlayer.getTrackCount()
```

## Advanced Features

### Custom Events
```lua
-- Track change event
function onTrackChange(trackIndex, trackInfo)
    print("Now playing: " .. trackInfo.title)
end

-- Playback end event
function onPlaybackEnd()
    print("Playlist finished")
end
```

### Progress Tracking
```lua
-- Get current position
local position = MusicPlayer.getPosition()

-- Seek to position (in seconds)
MusicPlayer.seek(30)

-- Get track duration
local duration = MusicPlayer.getDuration()
```

### Visualization
```lua
-- Show track progress bar
MusicPlayer.showProgress(true)

-- Display track time
MusicPlayer.showTime(true)

-- Custom track display
function displayTrackInfo(track)
    local info = string.format("%s - %s", track.artist, track.title)
    Player.broadcast(info, {1,1,1})
end
```

## Object Configuration

### Object Properties
```lua
-- Set object nickname
self.setName("My Playlist")

-- Set description
self.setDescription("Custom music collection")

-- Set custom image
self.setCustomObject({
    image = "https://example.com/image.jpg",
    image_secondary = "https://example.com/back.jpg"
})
```

### Interaction Settings
```lua
-- Enable/disable interaction
self.interactable = true

-- Set tooltip
self.tooltip = true

-- Custom context menu
function onObjectEnterContainer(container)
    if container == self then
        MusicPlayer.showControls()
    end
end
```

## Integration Patterns

### Scene Integration
1. **Background Music**: Global script for ambient music
2. **Object-Based**: Specific objects trigger music
3. **Zone-Based**: Music changes based on player location
4. **Event-Driven**: Music triggered by game events

### Multi-Player Considerations
```lua
-- Sync music for all players
MusicPlayer.syncAll(true)

-- Per-player music control
if Player.color == "Red" then
    MusicPlayer.setVolume(0.8)
end

-- Host-only controls
if Player.host then
    MusicPlayer.showAdminControls()
end
```

### Save File Integration
```lua
-- Auto-start music on load
function onLoad()
    Wait.time(function()
        MusicPlayer.play()
    end, 2)
end

-- Save current state
function onSave()
    local data = {
        currentTrack = MusicPlayer.getCurrentTrack(),
        volume = MusicPlayer.getVolume(),
        position = MusicPlayer.getPosition()
    }
    return JSON.encode(data)
end
```

## Troubleshooting TTS Integration

### Common Issues

#### Audio Not Playing
- Verify URLs are publicly accessible
- Check TTS audio settings
- Test URLs in a web browser
- Ensure HTTPS URLs when possible

#### Scripts Not Loading
- Check for syntax errors in Lua code
- Verify script placement (Global vs Object)
- Check TTS scripting permissions
- Validate JSON formatting for save files

#### Images Not Displaying
- Verify image URLs are accessible
- Check image format (JPG, PNG, GIF)
- Ensure proper image dimensions
- Test URLs in browser

### Performance Optimization

#### Large Playlists
```lua
-- Load tracks on demand
function loadTrackBatch(startIndex, count)
    -- Load only needed tracks
end

-- Paginated track display
function showTrackPage(page)
    local startIndex = (page - 1) * 10 + 1
    -- Show 10 tracks per page
end
```

#### Memory Management
```lua
-- Clean up unused audio
function cleanup()
    MusicPlayer.clearCache()
end

-- Optimize object references
local playerRef = MusicPlayer
```

## Best Practices

### Code Organization
- Use modules for complex music systems
- Separate configuration from logic
- Comment important functions
- Use meaningful variable names

### User Experience
- Provide clear visual feedback
- Include help commands
- Handle errors gracefully
- Test with multiple players

### Performance
- Cache frequently used data
- Minimize global script complexity
- Use local variables when possible
- Clean up resources properly

## Examples

### Simple Music Player
```lua
-- Basic music player with essential controls
local MusicPlayer = {
    tracks = {
        {artist = "Artist 1", title = "Song 1", url = "https://..."},
        {artist = "Artist 2", title = "Song 2", url = "https://..."}
    },
    currentTrack = 1,
    isPlaying = false
}

function MusicPlayer.play()
    if #self.tracks > 0 then
        local track = self.tracks[self.currentTrack]
        Music.play(track.url)
        self.isPlaying = true
        print("Playing: " .. track.artist .. " - " .. track.title)
    end
end

function MusicPlayer.next()
    if self.currentTrack < #self.tracks then
        self.currentTrack = self.currentTrack + 1
        self.play()
    end
end
```

### Advanced Player with UI
```lua
-- Music player with custom UI and advanced features
function createMusicPlayerUI()
    local ui = {
        {tag="Panel", attributes={width=400, height=300}},
        {tag="Text", attributes={text="Music Player"}, value="Music Player"},
        {tag="Button", attributes={onClick="playButton"}, value="Play"},
        {tag="Button", attributes={onClick="nextButton"}, value="Next"}
    }
    
    Player.White.createUI(ui)
end

function playButton()
    MusicPlayer.play()
    updateUI()
end

function updateUI()
    local track = MusicPlayer.getCurrentTrack()
    UI.setAttribute("trackDisplay", "text", track.artist .. " - " .. track.title)
end
```

## Community Resources

### TTS Scripting Documentation
- [Official TTS API Reference](https://api.tabletopsimulator.com/)
- [Lua Scripting Guide](https://www.lua.org/manual/)
- [TTS Community Forums](https://forums.tabletopsimulator.com/)

### Music Integration Examples
- Browse Steam Workshop for music-enabled mods
- Study existing music player implementations
- Join TTS Discord communities for help

### Development Tools
- TTS Atom Plugin for script editing
- Online Lua validators
- JSON formatters and validators
- Image hosting services for custom images
