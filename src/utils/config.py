"""
Configuration and Utilities Module

This module provides configuration management and utility functions
for the TTSMixmaster application.
"""

import os
import json
import logging
from pathlib import Path
from typing import Dict, Any, Optional, List
from dataclasses import dataclass, asdict
import configparser



@dataclass
class AppConfig:
    """Application configuration settings"""
    # LastFM API Settings
    lastfm_api_key: str = ""
    lastfm_api_secret: str = ""
    lastfm_username: str = ""
    
    # YouTube API Settings
    youtube_api_key: str = ""
    youtube_channel_id: str = ""
    
    # Spotify API Settings
    spotify_client_id: str = ""
    spotify_client_secret: str = ""
    spotify_user_id: str = ""
    spotify_redirect_uri: str = "http://localhost:8888/callback"
    spotify_use_oauth: bool = True
    
    # Legacy Steam API Settings (for backwards compatibility)
    steam_api_key: str = ""
    steam_username: str = ""
    
    # Azure Storage Settings
    azure_storage_connection_string: str = ""
    azure_container_name: str = "tts-audio"
    
    # Path Settings
    download_path: str = "./downloads"
    upload_path: str = "./uploads"
    tts_output_path: str = "./tts_formatted"
    
    # Audio Settings
    audio_quality: str = "192"
    audio_format: str = "mp3"
    
    # UI Settings
    theme: str = "dark"
    window_size: str = "1200x800"
    
    def to_dict(self) -> Dict[str, Any]:
        """Convert to dictionary"""
        return asdict(self)
    
    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> 'AppConfig':
        """Create from dictionary"""
        return cls(**data)


class ConfigManager:
    """Manages application configuration"""
    
    def __init__(self, config_file: str = "config.json"):
        """
        Initialize configuration manager
        
        Args:
            config_file: Path to configuration file
        """
        self.config_file = Path(config_file)
        self.config = AppConfig()
        
        # Load configuration
        self.load_config()
    
    def load_config(self):
        """Load configuration from file"""
        # Load from file if it exists
        if self.config_file.exists():
            try:
                with open(self.config_file, 'r') as f:
                    data = json.load(f)
                    self.config = AppConfig.from_dict(data)
            except Exception as e:
                logging.warning(f"Failed to load config file: {e}")
    

    def save_config(self):
        """Save configuration to file"""
        try:
            with open(self.config_file, 'w') as f:
                json.dump(self.config.to_dict(), f, indent=2)
        except Exception as e:
            logging.error(f"Failed to save config file: {e}")
    
    def get_config(self) -> AppConfig:
        """Get current configuration"""
        return self.config
    
    def update_config(self, **kwargs):
        """Update configuration values"""
        for key, value in kwargs.items():
            if hasattr(self.config, key):
                setattr(self.config, key, value)
        self.save_config()
    
    def is_configured(self) -> bool:
        """Check if essential configuration is present"""
        return bool(self.config.lastfm_api_key and self.config.lastfm_username)


def setup_logging(log_level: str = "INFO", log_file: str = "ttsmixmaster.log"):
    """
    Set up logging configuration
    
    Args:
        log_level: Logging level (DEBUG, INFO, WARNING, ERROR)
        log_file: Log file path
    """
    logging.basicConfig(
        level=getattr(logging, log_level.upper()),
        format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
        handlers=[
            logging.FileHandler(log_file),
            logging.StreamHandler()
        ]
    )


def create_directories(config: AppConfig):
    """
    Create necessary directories based on configuration
    
    Args:
        config: Application configuration
    """
    directories = [
        config.download_path,
        config.upload_path,
        config.tts_output_path
    ]
    
    for directory in directories:
        Path(directory).mkdir(parents=True, exist_ok=True)


def validate_audio_quality(quality: str) -> bool:
    """
    Validate audio quality setting
    
    Args:
        quality: Audio quality string
        
    Returns:
        True if valid, False otherwise
    """
    valid_qualities = ["128", "192", "256", "320"]
    return quality in valid_qualities


def sanitize_filename(filename: str, max_length: int = 200) -> str:
    """
    Sanitize filename for filesystem compatibility
    
    Args:
        filename: Original filename
        max_length: Maximum filename length
        
    Returns:
        Sanitized filename
    """
    import re
    
    # Remove invalid characters
    filename = re.sub(r'[<>:"/\\|?*]', '', filename)
    # Replace multiple spaces with single space
    filename = re.sub(r'\s+', ' ', filename)
    # Trim whitespace
    filename = filename.strip()
    # Limit length
    if len(filename) > max_length:
        filename = filename[:max_length]
    
    return filename


def format_duration(seconds: float) -> str:
    """
    Format duration in seconds to human-readable format
    
    Args:
        seconds: Duration in seconds
        
    Returns:
        Formatted duration string (e.g., "3:45")
    """
    if seconds <= 0:
        return "0:00"
    
    minutes = int(seconds // 60)
    seconds = int(seconds % 60)
    
    if minutes >= 60:
        hours = minutes // 60
        minutes = minutes % 60
        return f"{hours}:{minutes:02d}:{seconds:02d}"
    else:
        return f"{minutes}:{seconds:02d}"


def format_file_size(size_bytes: int) -> str:
    """
    Format file size in bytes to human-readable format
    
    Args:
        size_bytes: File size in bytes
        
    Returns:
        Formatted file size string (e.g., "2.5 MB")
    """
    if size_bytes == 0:
        return "0 B"
    
    size_names = ["B", "KB", "MB", "GB", "TB"]
    size_index = 0
    size = float(size_bytes)
    
    while size >= 1024 and size_index < len(size_names) - 1:
        size /= 1024
        size_index += 1
    
    return f"{size:.1f} {size_names[size_index]}"


def get_file_hash(file_path: str, algorithm: str = "md5") -> Optional[str]:
    """
    Get hash of a file
    
    Args:
        file_path: Path to the file
        algorithm: Hash algorithm (md5, sha1, sha256)
        
    Returns:
        File hash or None if error
    """
    import hashlib
    
    try:
        hash_func = getattr(hashlib, algorithm.lower())()
        
        with open(file_path, 'rb') as f:
            for chunk in iter(lambda: f.read(4096), b""):
                hash_func.update(chunk)
        
        return hash_func.hexdigest()
    except Exception:
        return None


def is_audio_file(file_path: str) -> bool:
    """
    Check if file is an audio file based on extension
    
    Args:
        file_path: Path to the file
        
    Returns:
        True if it's an audio file, False otherwise
    """
    audio_extensions = {
        '.mp3', '.wav', '.flac', '.ogg', '.m4a', '.aac', 
        '.wma', '.opus', '.mp4', '.m4p', '.3gp'
    }
    
    return Path(file_path).suffix.lower() in audio_extensions


class ProgressTracker:
    """Simple progress tracking utility"""
    
    def __init__(self, total: int, description: str = "Progress"):
        """
        Initialize progress tracker
        
        Args:
            total: Total number of items
            description: Description of the progress
        """
        self.total = total
        self.current = 0
        self.description = description
    
    def update(self, increment: int = 1):
        """Update progress"""
        self.current += increment
        if self.current > self.total:
            self.current = self.total
    
    def get_percentage(self) -> float:
        """Get progress percentage"""
        if self.total == 0:
            return 100.0
        return (self.current / self.total) * 100
    
    def is_complete(self) -> bool:
        """Check if progress is complete"""
        return self.current >= self.total
    
    def reset(self):
        """Reset progress"""
        self.current = 0
    
    def __str__(self) -> str:
        """String representation"""
        percentage = self.get_percentage()
        return f"{self.description}: {self.current}/{self.total} ({percentage:.1f}%)"


def get_playlist_folder_path(base_path: str, playlist_name: str) -> Path:
    """
    Get the folder path for a specific playlist
    
    Args:
        base_path: Base download/upload path
        playlist_name: Name of the playlist
        
    Returns:
        Path object for the playlist folder
    """
    sanitized_name = sanitize_filename(playlist_name)
    return Path(base_path) / sanitized_name


def create_playlist_directories(config: AppConfig, playlist_name: str) -> Dict[str, Path]:
    """
    Create necessary directories for a playlist
    
    Args:
        config: Application configuration
        playlist_name: Name of the playlist
        
    Returns:
        Dictionary with paths for downloads, uploads, and TTS output
    """
    playlist_paths = {
        'download': get_playlist_folder_path(config.download_path, playlist_name),
        'upload': get_playlist_folder_path(config.upload_path, playlist_name), 
        'tts_output': get_playlist_folder_path(config.tts_output_path, playlist_name)
    }
    
    # Create all directories
    for path in playlist_paths.values():
        path.mkdir(parents=True, exist_ok=True)
    
    return playlist_paths


def save_playlist_manifest(playlist_folder: Path, playlist_info, tracks_downloaded: List = None) -> Path:
    """
    Save a text manifest of playlist tracks for completeness checking
    
    Args:
        playlist_folder: Path to the playlist folder
        playlist_info: PlaylistInfo object
        tracks_downloaded: List of successfully downloaded tracks
        
    Returns:
        Path to the created manifest file
    """
    from ..api.base_service import PlaylistInfo, Track
    
    manifest_path = playlist_folder / "playlist_manifest.txt"
    
    with open(manifest_path, 'w', encoding='utf-8') as f:
        f.write(f"Playlist: {playlist_info.name}\n")
        f.write(f"Service: {playlist_info.service_type.value}\n")
        f.write(f"Total Tracks: {len(playlist_info.tracks)}\n")
        if tracks_downloaded is not None:
            f.write(f"Downloaded: {len(tracks_downloaded)}\n")
        f.write(f"Created: {format_duration(0)}\n\n")  # Will use current time
        
        f.write("=== TRACK LIST ===\n")
        for i, track in enumerate(playlist_info.tracks, 1):
            status = ""
            if tracks_downloaded is not None:
                status = " ✓" if track in tracks_downloaded else " ✗"
            f.write(f"{i:3d}. {track.artist} - {track.title}{status}\n")
            if track.album:
                f.write(f"     Album: {track.album}\n")
        
        if tracks_downloaded is not None:
            f.write(f"\n=== DOWNLOAD SUMMARY ===\n")
            f.write(f"Successfully downloaded: {len(tracks_downloaded)}/{len(playlist_info.tracks)} tracks\n")
            failed_tracks = [t for t in playlist_info.tracks if t not in tracks_downloaded]
            if failed_tracks:
                f.write(f"\nFailed downloads:\n")
                for track in failed_tracks:
                    f.write(f"- {track.artist} - {track.title}\n")
    
    return manifest_path


def get_playlist_completeness(playlist_folder: Path) -> Dict[str, Any]:
    """
    Check completeness of a downloaded playlist
    
    Args:
        playlist_folder: Path to the playlist folder
        
    Returns:
        Dictionary with completeness information
    """
    manifest_path = playlist_folder / "playlist_manifest.txt"
    
    if not manifest_path.exists():
        return {"error": "No manifest file found"}
    
    # Count audio files in the folder
    audio_files = [f for f in playlist_folder.iterdir() if is_audio_file(str(f))]
    
    # Parse manifest for expected track count
    try:
        with open(manifest_path, 'r', encoding='utf-8') as f:
            content = f.read()
            
        # Extract total tracks from manifest
        import re
        total_match = re.search(r"Total Tracks:\s*(\d+)", content)
        total_tracks = int(total_match.group(1)) if total_match else 0
        
        # Extract downloaded count if present
        downloaded_match = re.search(r"Downloaded:\s*(\d+)", content)
        downloaded_tracks = int(downloaded_match.group(1)) if downloaded_match else len(audio_files)
        
        return {
            "total_expected": total_tracks,
            "downloaded": downloaded_tracks,
            "audio_files_found": len(audio_files),
            "completion_rate": (len(audio_files) / total_tracks * 100) if total_tracks > 0 else 0,
            "is_complete": len(audio_files) >= total_tracks,
            "manifest_path": str(manifest_path)
        }
        
    except Exception as e:
        return {"error": f"Failed to parse manifest: {e}"}


def create_backup(file_path: str, backup_dir: str = "./backups") -> Optional[str]:
    """
    Create a backup of a file
    
    Args:
        file_path: Path to the file to backup
        backup_dir: Directory to store backups
        
    Returns:
        Path to backup file or None if error
    """
    import shutil
    from datetime import datetime
    
    try:
        source_path = Path(file_path)
        if not source_path.exists():
            return None
        
        backup_path = Path(backup_dir)
        backup_path.mkdir(parents=True, exist_ok=True)
        
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        backup_filename = f"{source_path.stem}_{timestamp}{source_path.suffix}"
        backup_file = backup_path / backup_filename
        
        shutil.copy2(source_path, backup_file)
        return str(backup_file)
    except Exception:
        return None
