# API Reference

> **Legacy Python implementation, kept as a reference for the .NET port.** The shipped app is the
> .NET/WinUI app in `src/TTSMixmaster.App` and `src/TTSMixmaster.Core` (see `AGENTS.md`).

## Overview

TTSMixmaster integrates with multiple music service APIs to provide comprehensive playlist and collection access. This document provides detailed information about each supported API.

## Supported Services

### Last.fm API
- **Purpose**: Access user music collections (top tracks, loved tracks, recent tracks)
- **API Version**: 2.0
- **Rate Limits**: 5 requests per second per IP
- **Authentication**: API key and secret

### YouTube Data API v3
- **Purpose**: Access YouTube playlists, liked videos, and public content
- **API Version**: v3
- **Rate Limits**: 10,000 units per day (default quota)
- **Authentication**: API key

### Spotify Web API
- **Purpose**: Access Spotify playlists and user content
- **API Version**: v1
- **Rate Limits**: Varies by endpoint
- **Authentication**: Client credentials or authorization code flow

## Service Details

### Last.fm API

#### Endpoints Used
- `user.getTopTracks` - Get user's top tracks
- `user.getLovedTracks` - Get user's loved tracks
- `user.getRecentTracks` - Get user's recent tracks
- `user.getInfo` - Get user information

#### Data Structures
```python
class Track:
    artist: str
    title: str
    album: Optional[str]
    duration: Optional[int]
    play_count: Optional[int]
    loved: Optional[bool]
    url: Optional[str]
```

#### Configuration
```env
LASTFM_API_KEY=your_api_key
LASTFM_API_SECRET=your_api_secret
LASTFM_USERNAME=your_username
```

#### Usage Patterns
- Collections, not traditional playlists
- Time-based filtering (7day, 1month, overall, etc.)
- Configurable limits (1-1000 tracks)

### YouTube Data API v3

#### Endpoints Used
- `playlists` - List user playlists
- `playlistItems` - Get playlist contents
- `videos` - Get video details
- `search` - Search public playlists

#### Quota Costs
- Playlist list: 1 unit
- Playlist items: 1 unit
- Video details: 1 unit
- Search: 100 units

#### Configuration
```env
YOUTUBE_API_KEY=your_api_key
YOUTUBE_CHANNEL_ID=your_channel_id  # Optional
```

#### Usage Patterns
- Personal playlists (requires Channel ID)
- Public playlist search
- Liked videos collection
- Video metadata extraction

### Spotify Web API

#### Endpoints Used
- `me/playlists` - Get user playlists
- `playlists/{id}/tracks` - Get playlist tracks
- `search` - Search public playlists

#### Authentication Flow
1. Client Credentials (for public content)
2. Authorization Code (for user content)

#### Configuration
```env
SPOTIFY_CLIENT_ID=your_client_id
SPOTIFY_CLIENT_SECRET=your_client_secret
SPOTIFY_USER_ID=your_user_id  # Optional
```

#### Usage Patterns
- Personal playlists (requires authentication)
- Public playlist discovery
- Track metadata with audio features

## Data Models

### Common Track Structure
```python
@dataclass
class Track:
    artist: str
    title: str
    album: Optional[str] = None
    duration: Optional[int] = None
    url: Optional[str] = None
    external_urls: Dict[str, str] = field(default_factory=dict)
    metadata: Dict[str, Any] = field(default_factory=dict)
```

### Playlist Information
```python
@dataclass
class PlaylistInfo:
    id: str
    name: str
    description: Optional[str] = None
    track_count: Optional[int] = None
    owner: Optional[str] = None
    public: Optional[bool] = None
    service: ServiceType = ServiceType.UNKNOWN
    external_urls: Dict[str, str] = field(default_factory=dict)
```

### Service Types
```python
class ServiceType(Enum):
    LASTFM = "lastfm"
    YOUTUBE = "youtube"
    SPOTIFY = "spotify"
    UNKNOWN = "unknown"
```

## Rate Limiting

### Last.fm
- 5 requests per second per IP address
- No daily limits
- Automatic retry with exponential backoff

### YouTube
- 10,000 units per day (default quota)
- Can request quota increases
- Different operations cost different units

### Spotify
- Rate limits vary by endpoint
- Typically 429 status code when exceeded
- Automatic retry with Retry-After header

## Error Handling

### Common Error Types
- `AuthenticationError`: Invalid credentials
- `RateLimitError`: Rate limit exceeded
- `NotFoundError`: Resource not found
- `ServiceUnavailableError`: Service temporarily unavailable

### Error Response Format
```python
@dataclass
class APIError:
    service: ServiceType
    error_type: str
    message: str
    status_code: Optional[int] = None
    retry_after: Optional[int] = None
```

## Best Practices

### API Usage
1. **Cache Results**: Store API responses to reduce calls
2. **Batch Requests**: Use batch endpoints when available
3. **Respect Rate Limits**: Implement proper rate limiting
4. **Handle Errors Gracefully**: Provide fallbacks for failures

### Authentication
1. **Secure Storage**: Store credentials securely
2. **Token Refresh**: Implement token refresh for OAuth
3. **Minimal Scopes**: Request only necessary permissions
4. **Credential Rotation**: Regularly update API keys

### Performance
1. **Async Requests**: Use asynchronous requests when possible
2. **Connection Pooling**: Reuse HTTP connections
3. **Timeout Handling**: Set appropriate timeouts
4. **Pagination**: Handle large datasets properly

## Service Limitations

### Last.fm
- No true playlist support, only collections
- Limited real-time data
- Some users may have private profiles

### YouTube
- Quota limitations can be restrictive
- Video availability varies by region
- Some content is age-restricted

### Spotify
- Requires app registration
- Some features need user authentication
- Regional content restrictions

## Migration and Compatibility

### API Version Updates
- Monitor for deprecation notices
- Test with new API versions
- Maintain backward compatibility

### Service Changes
- Handle service outages gracefully
- Provide alternative data sources
- Cache critical data locally

## Implementation Examples

### Last.fm Track Fetching
```python
async def get_user_top_tracks(username: str, period: str = "overall", limit: int = 50):
    params = {
        'method': 'user.gettoptracks',
        'user': username,
        'period': period,
        'limit': limit,
        'api_key': self.api_key,
        'format': 'json'
    }
    response = await self.session.get(self.base_url, params=params)
    return self._parse_tracks(response.json())
```

### YouTube Playlist Fetching
```python
async def get_playlist_items(playlist_id: str):
    params = {
        'part': 'snippet,contentDetails',
        'playlistId': playlist_id,
        'maxResults': 50,
        'key': self.api_key
    }
    response = await self.session.get(f"{self.base_url}/playlistItems", params=params)
    return self._parse_playlist_items(response.json())
```

### Spotify Playlist Fetching
```python
async def get_user_playlists(user_id: str):
    headers = {'Authorization': f'Bearer {self.access_token}'}
    response = await self.session.get(
        f"{self.base_url}/users/{user_id}/playlists",
        headers=headers
    )
    return self._parse_playlists(response.json())
```

## Testing and Validation

### API Testing
- Use test endpoints when available
- Validate responses against schemas
- Test error conditions
- Monitor API health

### Integration Testing
- Test with real API credentials
- Validate data consistency
- Test rate limiting behavior
- Verify error handling
