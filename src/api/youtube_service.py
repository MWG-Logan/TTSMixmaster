"""
YouTube API Integration Module

This module provides functionality to interact with the YouTube Data API v3
to retrieve playlists and track information.
"""

from typing import List, Optional, Dict, Any
from googleapiclient.discovery import build
from googleapiclient.errors import HttpError
import isodate
import logging
from collections import OrderedDict

from .base_service import BaseMusicService, Track, PlaylistInfo, ServiceType, PlaylistType


logger = logging.getLogger(__name__)


class YouTubeService(BaseMusicService):
    """YouTube API client for retrieving playlists and videos"""

    def __init__(self, api_key: str, channel_id: str = ""):
        """
        Initialize the YouTube API client

        Args:
            api_key: YouTube Data API v3 key
            channel_id: Default channel ID for user operations
        """
        super().__init__(ServiceType.YOUTUBE)
        self.api_key = api_key
        self.channel_id = channel_id
        self.youtube = None

        # Simple in-memory caches to reduce repeated API calls.
        # These are per-service-instance; they reset when the app restarts.
        self._video_details_cache: Dict[str, Dict[str, Any]] = {}
        self._official_search_cache: "OrderedDict[str, Optional[Dict[str, Any]]]" = OrderedDict()
        self._official_search_cache_max_entries: int = 2000

        if api_key:
            try:
                self.youtube = build('youtube', 'v3', developerKey=api_key)
                logger.info("YouTube API initialized successfully")
            except Exception as e:
                logger.exception("Failed to initialize YouTube API")
                self.youtube = None
        else:
            logger.warning("YouTube API key not provided - service will not be available")

    def test_connection(self) -> bool:
        """Test if the YouTube connection is working"""
        try:
            if not self.youtube:
                logger.warning("YouTube API client not initialized")
                return False

            logger.info("Testing YouTube API connection...")
            self.youtube.search().list(
                part='snippet',
                q='test',
                maxResults=1,
                type='video'
            ).execute()

            logger.info("YouTube API connection successful")
            return True
        except Exception as e:
            logger.exception("YouTube API connection failed")
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
            **kwargs:
                max_results: Maximum tracks to load (default None = full playlist)
                max_pages: Maximum playlistItems pages to request (default None = derived)
                prefer_official: If True, attempt to replace Topic uploads with "official" uploads (default False)
                max_official_searches: Hard cap on official-search API calls per playlist load (default 0)

        Returns:
            PlaylistInfo with tracks loaded
        """
        if not self.youtube:
            raise Exception("YouTube API not initialized")

        max_results_raw = kwargs.get('max_results', None)
        max_results: Optional[int]
        if max_results_raw is None:
            max_results = None
        else:
            max_results = int(max_results_raw)

        prefer_official = bool(kwargs.get('prefer_official', False))
        max_official_searches = int(kwargs.get('max_official_searches', 0))
        max_pages_raw = kwargs.get('max_pages')

        # Safety guardrail to prevent accidentally loading an absurd amount of data.
        # Can be overridden by explicitly setting max_results.
        hard_max_tracks_default = 5000

        try:
            # Get playlist info
            playlist_response = self.youtube.playlists().list(
                part='snippet,contentDetails',
                id=playlist_id
            ).execute()

            if not playlist_response.get('items'):
                raise ValueError(f"Playlist not found: {playlist_id}")

            playlist_data = playlist_response['items'][0]
            playlist_item_count = int(playlist_data.get('contentDetails', {}).get('itemCount', 0) or 0)

            if max_results is None:
                # Load the full playlist (or the hard cap if itemCount is missing/huge)
                target_tracks = playlist_item_count if playlist_item_count > 0 else hard_max_tracks_default
                target_tracks = min(target_tracks, hard_max_tracks_default)
            else:
                target_tracks = max(0, max_results)

            # If max_pages isn't provided, derive a safe upper bound from target_tracks.
            if max_pages_raw is None:
                max_pages = max(1, (target_tracks + 49) // 50) if target_tracks > 0 else 1
            else:
                max_pages = max(1, int(max_pages_raw))

            tracks: List[Track] = []
            next_page_token: Optional[str] = None
            pages_loaded = 0
            official_searches_used = 0

            while (len(tracks) < target_tracks) and (pages_loaded < max_pages):
                request = self.youtube.playlistItems().list(
                    part='snippet,contentDetails',
                    playlistId=playlist_id,
                    maxResults=min(50, target_tracks - len(tracks)),
                    pageToken=next_page_token
                )

                response = request.execute()
                pages_loaded += 1

                items = response.get('items', [])
                if not items:
                    break

                video_ids = [item.get('contentDetails', {}).get('videoId', '') for item in items]
                video_ids = [vid for vid in video_ids if vid]
                video_details = self._get_video_details(video_ids)

                for item in items:
                    if len(tracks) >= target_tracks:
                        break

                    video_id = item.get('contentDetails', {}).get('videoId', '')
                    if not video_id:
                        continue

                    video_detail = video_details.get(video_id, {})

                    duration = 0
                    if (
                        'contentDetails' in video_detail
                        and isinstance(video_detail.get('contentDetails'), dict)
                        and 'duration' in video_detail['contentDetails']
                    ):
                        try:
                            duration_obj = isodate.parse_duration(video_detail['contentDetails']['duration'])
                            duration = int(duration_obj.total_seconds())
                        except Exception:
                            duration = 0

                    snippet = item.get('snippet', {})
                    raw_channel = snippet.get('videoOwnerChannelTitle') or snippet.get('channelTitle') or ""
                    video_title = snippet.get('title') or ""

                    parsed_artist, parsed_title = self._parse_track_from_title(video_title, raw_channel)

                    if (
                        prefer_official
                        and max_official_searches > 0
                        and official_searches_used < max_official_searches
                        and raw_channel.endswith(" - Topic")
                    ):
                        official_version = self._search_for_official_version(parsed_artist, parsed_title)
                        official_searches_used += 1
                        if official_version:
                            official_channel = official_version.get('snippet', {}).get('channelTitle', '')
                            official_title_str = official_version.get('snippet', {}).get('title', '')
                            official_artist, official_title = self._parse_track_from_title(
                                official_title_str,
                                official_channel,
                            )
                            parsed_artist = official_artist
                            parsed_title = official_title

                    track = Track(
                        title=parsed_title,
                        artist=parsed_artist,
                        duration=duration,
                        url=f"https://www.youtube.com/watch?v={video_id}",
                        service_id=video_id,
                        service_type=ServiceType.YOUTUBE,
                        thumbnail_url=snippet.get('thumbnails', {}).get('medium', {}).get('url', ''),
                    )
                    tracks.append(track)

                next_page_token = response.get('nextPageToken')
                if not next_page_token:
                    break

            return PlaylistInfo(
                name=playlist_data.get('snippet', {}).get('title', ""),
                tracks=tracks,
                description=playlist_data.get('snippet', {}).get('description', ""),
                service_type=ServiceType.YOUTUBE,
                playlist_type=PlaylistType.YOUTUBE_PLAYLIST,
                service_id=playlist_id,
                thumbnail_url=playlist_data.get('snippet', {}).get('thumbnails', {}).get('medium', {}).get('url', ''),
                owner=playlist_data.get('snippet', {}).get('channelTitle', ""),
                total_tracks=len(tracks),
            )

        except HttpError as e:
            raise Exception(f"YouTube API error: {e}")
    
    def _get_video_details(self, video_ids: List[str]) -> Dict[str, Any]:
        """Get detailed information for multiple videos (cached by video_id)."""
        if not video_ids or not self.youtube:
            return {}
        
        # De-dupe and request only cache misses.
        unique_ids = list(dict.fromkeys(video_ids))
        missing_ids = [vid for vid in unique_ids if vid not in self._video_details_cache]

        if missing_ids:
            try:
                response = self.youtube.videos().list(
                    part='contentDetails,statistics',
                    id=','.join(missing_ids)
                ).execute()

                for item in response.get('items', []):
                    video_id = item.get('id')
                    if video_id:
                        self._video_details_cache[video_id] = item

            except HttpError:
                # Return whatever we have from cache.
                pass

        return {vid: self._video_details_cache.get(vid, {}) for vid in unique_ids}

    def _normalize_official_search_key(self, artist: str, title: str) -> str:
        return f"{artist.strip().lower()}::{title.strip().lower()}"

    def _search_for_official_version(self, artist: str, title: str) -> Optional[Dict[str, Any]]:
        """
        Search for an official version of a track, preferring official channels.

        Note: this result is cached by (artist,title) to avoid repeated searches.
        """
        if not self.youtube:
            return None

        cache_key = self._normalize_official_search_key(artist, title)
        if cache_key in self._official_search_cache:
            # LRU refresh
            cached = self._official_search_cache.pop(cache_key)
            self._official_search_cache[cache_key] = cached
            return cached

        try:
            search_query = f"{artist} {title}".strip()
            if not search_query:
                return None

            response = self.youtube.search().list(
                part='snippet',
                q=search_query,
                maxResults=10,
                type='video',
                order='relevance'
            ).execute()

            videos = response.get('items', [])
            official_videos: List[Dict[str, Any]] = []
            topic_videos: List[Dict[str, Any]] = []
            other_videos: List[Dict[str, Any]] = []

            for video in videos:
                channel_title = video.get('snippet', {}).get('channelTitle', '')
                if self._is_official_channel(channel_title):
                    official_videos.append(video)
                elif channel_title.endswith(" - Topic"):
                    topic_videos.append(video)
                else:
                    other_videos.append(video)

            preferred_order = official_videos + other_videos + topic_videos
            result = preferred_order[0] if preferred_order else None

        except Exception:
            logger.exception("Error searching for official version")
            result = None

        # Cache with bounded size (simple LRU).
        self._official_search_cache[cache_key] = result
        if len(self._official_search_cache) > self._official_search_cache_max_entries:
            self._official_search_cache.popitem(last=False)

        return result

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
                title = re.sub(r'\s*\[Official.*?]', '', title, flags=re.IGNORECASE)
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
        title = re.sub(r'\s*\[Official.*?]', '', title, flags=re.IGNORECASE)
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
    
    def search_playlists(self, query: str, limit: int = 20) -> List[PlaylistInfo]:
        """Search for public YouTube playlists.

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
                maxResults=min(limit * 2, 50),
                type='playlist'
            ).execute()

            official_playlists: List[PlaylistInfo] = []
            other_playlists: List[PlaylistInfo] = []

            for item in response.get('items', []):
                snippet = item.get('snippet', {})
                channel_title = snippet.get('channelTitle', '')

                playlist_info = PlaylistInfo(
                    name=snippet.get('title', ''),
                    tracks=[],
                    description=snippet.get('description', ''),
                    service_type=ServiceType.YOUTUBE,
                    playlist_type=PlaylistType.YOUTUBE_PLAYLIST,
                    service_id=item.get('id', {}).get('playlistId', ''),
                    thumbnail_url=snippet.get('thumbnails', {}).get('medium', {}).get('url', ''),
                    owner=self._clean_artist_name(channel_title),
                )

                if self._is_official_channel(channel_title):
                    official_playlists.append(playlist_info)
                else:
                    other_playlists.append(playlist_info)

            preferred_playlists = official_playlists + other_playlists
            # Filter out entries missing IDs; then cap to requested limit.
            preferred_playlists = [p for p in preferred_playlists if p.service_id]
            return preferred_playlists[:limit]

        except HttpError as e:
            raise Exception(f"YouTube API error: {e}")
