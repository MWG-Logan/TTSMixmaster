"""
YouTube API Integration Module

This module provides functionality to interact with the YouTube Data API v3
to retrieve playlists and track information.
"""

import os
from typing import List, Optional, Dict, Any
from googleapiclient.discovery import build
from googleapiclient.errors import HttpError
import isodate

from .base_service import BaseMusicService, Track, PlaylistInfo, ServiceType, PlaylistType


class YouTubeService(BaseMusicService):
    """YouTube API client for retrieving playlists and videos"""
    
    def __init__(self, api_key: str, channel_id: str = ""):
        """
        Initialize the YouTube API client
        
        Args:
            api_key: YouTube Data API v3 key
            channel_id: Default channel ID for user operations        """
        super().__init__(ServiceType.YOUTUBE)
        self.api_key = api_key
        self.channel_id = channel_id
        self.youtube = None
        
        if api_key:
            try:
                self.youtube = build('youtube', 'v3', developerKey=api_key)
                print(f"YouTube API initialized successfully with key: {api_key[:10]}...")
            except Exception as e:
                print(f"Failed to initialize YouTube API: {e}")
                self.youtube = None
        else:
            print("YouTube API key not provided - service will not be available")
    
    def test_connection(self) -> bool:
        """Test if the YouTube connection is working"""
        try:
            if not self.youtube:
                print("YouTube API client not initialized")
                return False
            
            print("Testing YouTube API connection...")
            # Try a simple search to test the connection
            response = self.youtube.search().list(
                part='snippet',
                q='test',
                maxResults=1,
                type='video'
            ).execute()
            
            print("YouTube API connection successful")
            return True
        except Exception as e:
            print(f"YouTube API connection failed: {e}")
            return False
    
    def get_supported_playlist_types(self) -> List[PlaylistType]:
        """Get list of supported playlist types for YouTube"""
        return [
            PlaylistType.YOUTUBE_PLAYLIST,
            PlaylistType.YOUTUBE_LIKED_VIDEOS,
            PlaylistType.YOUTUBE_WATCH_LATER
        ]
    
    def get_user_playlists(self, **kwargs) -> List[PlaylistInfo]:
        """
        Get user's playlists from YouTube
        
        Args:
            **kwargs: Additional parameters like channel_id
            
        Returns:
            List of PlaylistInfo objects
        """
        if not self.youtube:
            raise Exception("YouTube API not initialized")
        
        channel_id = kwargs.get('channel_id', self.channel_id)
        if not channel_id:
            raise ValueError("Channel ID is required")
        
        try:
            playlists = []
            
            # Get user's playlists
            response = self.youtube.playlists().list(
                part='snippet,contentDetails',
                channelId=channel_id,
                maxResults=50
            ).execute()
            
            for playlist_data in response.get('items', []):
                playlist_info = PlaylistInfo(
                    name=playlist_data['snippet']['title'],
                    tracks=[],  # Tracks loaded on demand
                    description=playlist_data['snippet']['description'],
                    service_type=ServiceType.YOUTUBE,
                    playlist_type=PlaylistType.YOUTUBE_PLAYLIST,
                    service_id=playlist_data['id'],
                    thumbnail_url=playlist_data['snippet']['thumbnails'].get('medium', {}).get('url', ''),
                    owner=playlist_data['snippet']['channelTitle'],
                    total_tracks=playlist_data['contentDetails']['itemCount']
                )
                playlists.append(playlist_info)
            
            # Add special playlists
            special_playlists = [
                PlaylistInfo(
                    name="Liked Videos",
                    tracks=[],
                    description="Your liked videos on YouTube",
                    service_type=ServiceType.YOUTUBE,
                    playlist_type=PlaylistType.YOUTUBE_LIKED_VIDEOS,
                    service_id="LL",  # Special ID for liked videos
                    owner="You"
                ),
                PlaylistInfo(
                    name="Watch Later",
                    tracks=[],
                    description="Your Watch Later playlist",
                    service_type=ServiceType.YOUTUBE,
                    playlist_type=PlaylistType.YOUTUBE_WATCH_LATER,
                    service_id="WL",  # Special ID for watch later
                    owner="You"
                )
            ]
            
            playlists.extend(special_playlists)
            return playlists
            
        except HttpError as e:
            raise Exception(f"YouTube API error: {e}")
    
    def get_playlist_tracks(self, playlist_id: str, **kwargs) -> PlaylistInfo:
        """
        Get tracks from a specific YouTube playlist
        
        Args:
            playlist_id: YouTube playlist ID
            **kwargs: Additional parameters. Pass ``max_results`` to cap the
                number of tracks returned; if omitted, the entire playlist is
                fetched by paginating until no further pages remain.
            
        Returns:
            PlaylistInfo with tracks loaded
        """
        if not self.youtube:
            raise Exception("YouTube API not initialized")
        
        # When max_results is not provided, fetch the whole playlist.
        max_results = kwargs.get('max_results')
        fetch_all = max_results is None
        
        try:
            # Get playlist info
            playlist_response = self.youtube.playlists().list(
                part='snippet,contentDetails',
                id=playlist_id
            ).execute()
            
            if not playlist_response['items']:
                raise ValueError(f"Playlist not found: {playlist_id}")
            
            playlist_data = playlist_response['items'][0]
            
            # Get playlist items
            tracks = []
            next_page_token = None
            
            while fetch_all or len(tracks) < max_results:
                page_size = 50 if fetch_all else min(50, max_results - len(tracks))
                request = self.youtube.playlistItems().list(
                    part='snippet,contentDetails',
                    playlistId=playlist_id,
                    maxResults=page_size,
                    pageToken=next_page_token
                )
                
                response = request.execute()
                
                # Get video details for duration
                video_ids = [item['contentDetails']['videoId'] for item in response['items']]
                video_details = self._get_video_details(video_ids)
                
                for item in response['items']:
                    video_id = item['contentDetails']['videoId']
                    video_detail = video_details.get(video_id, {})
                    
                    # Parse duration
                    duration = 0
                    if 'contentDetails' in video_detail and 'duration' in video_detail['contentDetails']:
                        try:
                            duration_obj = isodate.parse_duration(video_detail['contentDetails']['duration'])
                            duration = int(duration_obj.total_seconds())
                        except:
                            pass
                    # Get cleaner artist and title using parsing methods
                    raw_channel = item['snippet'].get('videoOwnerChannelTitle') or item['snippet'].get('channelTitle', '')
                    video_title = item['snippet']['title']
                    
                    # Parse artist and title directly from the playlist item.
                    # We intentionally do NOT perform a per-track search().list() lookup
                    # here: each search costs 100 YouTube Data API quota units and
                    # quickly exhausts the daily quota. The playlist item already
                    # contains a direct video ID/URL that the downloader can use.
                    parsed_artist, parsed_title = self._parse_track_from_title(video_title, raw_channel)
                    
                    track = Track(
                        title=parsed_title,
                        artist=parsed_artist,
                        duration=duration,
                        url=f"https://www.youtube.com/watch?v={video_id}",
                        service_id=video_id,
                        service_type=ServiceType.YOUTUBE,
                        thumbnail_url=item['snippet']['thumbnails'].get('medium', {}).get('url', '')
                    )
                    tracks.append(track)
                
                next_page_token = response.get('nextPageToken')
                if not next_page_token:
                    break
            
            return PlaylistInfo(
                name=playlist_data['snippet']['title'],
                tracks=tracks,
                description=playlist_data['snippet']['description'],
                service_type=ServiceType.YOUTUBE,
                playlist_type=PlaylistType.YOUTUBE_PLAYLIST,
                service_id=playlist_id,
                thumbnail_url=playlist_data['snippet']['thumbnails'].get('medium', {}).get('url', ''),
                owner=playlist_data['snippet']['channelTitle'],
                total_tracks=len(tracks)
            )
            
        except HttpError as e:
            raise Exception(f"YouTube API error: {e}")
    
    def search_playlists(self, query: str, limit: int = 20) -> List[PlaylistInfo]:
        """
        Search for public YouTube playlists
        
        Args:
            query: Search query
            limit: Maximum number of results
            
        Returns:
            List of PlaylistInfo objects
        """
        if not self.youtube:
            raise Exception("YouTube API not initialized")
        
        try:
            response = self.youtube.search().list(
                part='snippet',
                q=query,
                maxResults=min(limit * 2, 50),  # Get more results to filter for official channels
                type='playlist'
            ).execute()
            
            playlists = []
            official_playlists = []
            other_playlists = []
            
            # Separate official and unofficial playlists
            for item in response['items']:
                channel_title = item['snippet']['channelTitle']
                playlist_info = PlaylistInfo(
                    name=item['snippet']['title'],
                    tracks=[],  # Tracks loaded on demand
                    description=item['snippet']['description'],
                    service_type=ServiceType.YOUTUBE,
                    playlist_type=PlaylistType.YOUTUBE_PLAYLIST,
                    service_id=item['id']['playlistId'],
                    thumbnail_url=item['snippet']['thumbnails'].get('medium', {}).get('url', ''),
                    owner=self._clean_artist_name(channel_title)
                )
                
                if self._is_official_channel(channel_title):
                    official_playlists.append(playlist_info)
                else:
                    other_playlists.append(playlist_info)
            
            # Prefer official playlists, then others
            preferred_playlists = official_playlists + other_playlists
            
            # Return up to the requested limit
            return preferred_playlists[:limit]
        except HttpError as e:
            raise Exception(f"YouTube API error: {e}")
    
    def _get_video_details(self, video_ids: List[str]) -> Dict[str, Any]:
        """Get detailed information for multiple videos"""
        if not video_ids or not self.youtube:
            return {}
        
        try:
            response = self.youtube.videos().list(
                part='contentDetails,statistics',
                id=','.join(video_ids)
            ).execute()
            
            details = {}
            for item in response['items']:
                details[item['id']] = item
            
            return details
            
        except HttpError:
            return {}
    
    def _clean_artist_name(self, channel_title: str) -> str:
        """
        Clean up artist names from YouTube channel titles
        
        Args:
            channel_title: Raw channel title from YouTube
            
        Returns:
            Cleaned artist name
        """
        if not channel_title:
            return ""
        
        # Remove common suffixes that YouTube adds to auto-generated channels
        suffixes_to_remove = [
            " - Topic",
            " - Auto-generated by YouTube",
            "VEVO",  # Keep VEVO as it's official
            "Official",
            "Records",
            "Music"
        ]
        
        cleaned = channel_title
        
        # Remove "- Topic" and similar auto-generated suffixes
        for suffix in [" - Topic", " - Auto-generated by YouTube"]:
            if cleaned.endswith(suffix):
                cleaned = cleaned[:-len(suffix)].strip()
        
        return cleaned
    
    def _parse_track_from_title(self, video_title: str, channel_title: str) -> tuple[str, str]:
        """
        Parse artist and song title from YouTube video title
        
        Args:
            video_title: YouTube video title
            channel_title: YouTube channel title
            
        Returns:
            Tuple of (artist, title)
        """
        import re
        
        # Common patterns in music video titles
        patterns = [
            # "Artist - Song Title"
            r'^(.+?)\s*[-–—]\s*(.+?)(?:\s*\(.*\))?(?:\s*\[.*\])?$',
            # "Song Title by Artist"
            r'^(.+?)\s+by\s+(.+?)(?:\s*\(.*\))?(?:\s*\[.*\])?$',
            # "Artist: Song Title"
            r'^(.+?):\s*(.+?)(?:\s*\(.*\))?(?:\s*\[.*\])?$',
            # "Artist | Song Title"
            r'^(.+?)\s*\|\s*(.+?)(?:\s*\(.*\))?(?:\s*\[.*\])?$',
        ]
        
        for pattern in patterns:
            match = re.match(pattern, video_title, re.IGNORECASE)
            if match:
                part1, part2 = match.groups()
                
                # For "Song by Artist" pattern, swap the order
                if "by" in pattern:
                    artist, title = part2.strip(), part1.strip()
                else:
                    artist, title = part1.strip(), part2.strip()
                
                # Clean up the parsed artist name
                artist = self._clean_artist_name(artist)
                
                # Remove common extra text from titles
                title = re.sub(r'\s*\(Official.*?\)', '', title, flags=re.IGNORECASE)
                title = re.sub(r'\s*\[Official.*?\]', '', title, flags=re.IGNORECASE)
                title = re.sub(r'\s*\(Music Video\)', '', title, flags=re.IGNORECASE)
                title = re.sub(r'\s*\(Lyric Video\)', '', title, flags=re.IGNORECASE)
                title = re.sub(r'\s*\(Audio\)', '', title, flags=re.IGNORECASE)
                title = title.strip()
                
                return artist, title
        
        # If no pattern matches, fall back to channel name and full title
        artist = self._clean_artist_name(channel_title)
        title = video_title
        
        # Clean up the title
        title = re.sub(r'\s*\(Official.*?\)', '', title, flags=re.IGNORECASE)
        title = re.sub(r'\s*\[Official.*?\]', '', title, flags=re.IGNORECASE)
        title = title.strip()
        
        return artist, title
    
    def _is_official_channel(self, channel_title: str) -> bool:
        """
        Check if a channel is likely an official artist channel
        
        Args:
            channel_title: YouTube channel title
            
        Returns:
            True if likely official, False otherwise
        """
        if not channel_title:
            return False
        
        # Auto-generated channels are not official
        if channel_title.endswith(" - Topic"):
            return False
        
        # Official indicators
        official_indicators = [
            "VEVO",
            "Official",
            "Records",
            "Music",
            "Entertainment"
        ]
        
        channel_lower = channel_title.lower()
        for indicator in official_indicators:
            if indicator.lower() in channel_lower:
                return True
        
        # If it doesn't end with "- Topic" and doesn't have other auto-generated indicators,
        # it's likely a real channel
        return not any(suffix in channel_title for suffix in [
            "- Auto-generated by YouTube",
            "Auto-Generated"
        ])

    # ...existing code...
