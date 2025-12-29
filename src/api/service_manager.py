"""
Service Manager

This module manages multiple music services and provides a unified interface.
"""

from typing import List, Dict, Optional, Any
from dataclasses import dataclass

from .base_service import BaseMusicService, PlaylistInfo, ServiceType, PlaylistType
from .lastfm_service import LastFMService
from .youtube_service import YouTubeService
from .spotify_service import SpotifyService


@dataclass
class ServiceConfig:
    """Configuration for a music service"""
    service_type: ServiceType
    enabled: bool = False
    credentials: Optional[Dict[str, str]] = None
    
    def __post_init__(self):
        if self.credentials is None:
            self.credentials = {}


class MusicServiceManager:
    """Manages multiple music services"""
    
    def __init__(self):
        self.services: Dict[ServiceType, BaseMusicService] = {}
        self.service_configs: Dict[ServiceType, ServiceConfig] = {
            ServiceType.LASTFM: ServiceConfig(ServiceType.LASTFM),
            ServiceType.YOUTUBE: ServiceConfig(ServiceType.YOUTUBE),
            ServiceType.SPOTIFY: ServiceConfig(ServiceType.SPOTIFY)
        }
    
    def configure_service(self, service_type: ServiceType, credentials: Dict[str, str]) -> bool:
        """
        Configure a service with credentials
        
        Args:
            service_type: Type of service to configure
            credentials: Dictionary containing service credentials
            
        Returns:
            True if configuration was successful
        """
        try:
            if service_type == ServiceType.LASTFM:
                service = LastFMService(
                    api_key=credentials.get('api_key', ''),
                    api_secret=credentials.get('api_secret', ''),
                    username=credentials.get('username', '')
                )
            elif service_type == ServiceType.YOUTUBE:
                service = YouTubeService(
                    api_key=credentials.get('api_key', ''),
                    channel_id=credentials.get('channel_id', '')
                )
            elif service_type == ServiceType.SPOTIFY:
                service = SpotifyService(
                    client_id=credentials.get('client_id', ''),
                    client_secret=credentials.get('client_secret', ''),
                    user_id=credentials.get('user_id', ''),
                    redirect_uri=credentials.get('redirect_uri', 'http://127.0.0.1:8888/callback'),
                    use_oauth=credentials.get('use_oauth', True)
                )
            else:
                return False
            
            # Test connection
            if service.test_connection():
                self.services[service_type] = service
                self.service_configs[service_type].enabled = True
                self.service_configs[service_type].credentials = credentials
                return True
            else:
                return False
                
        except Exception as e:
            print(f"Failed to configure {service_type.value} service: {e}")
            return False
    
    def get_enabled_services(self) -> List[ServiceType]:
        """Get list of enabled services"""
        return [st for st, config in self.service_configs.items() if config.enabled]
    
    def get_service(self, service_type: ServiceType) -> Optional[BaseMusicService]:
        """Get a specific service"""
        return self.services.get(service_type)
    
    def is_service_enabled(self, service_type: ServiceType) -> bool:
        """Check if a service is enabled"""
        return self.service_configs[service_type].enabled
    
    def get_user_playlists(self, service_type: ServiceType, **kwargs) -> List[PlaylistInfo]:
        """Get user playlists from a specific service"""
        service = self.get_service(service_type)
        if not service:
            raise ValueError(f"Service {service_type.value} is not configured")
        
        return service.get_user_playlists(**kwargs)
    
    def get_playlist_tracks(self, service_type: ServiceType, playlist_id: str, **kwargs) -> PlaylistInfo:
        """Get tracks from a playlist in a specific service"""
        service = self.get_service(service_type)
        if not service:
            raise ValueError(f"Service {service_type.value} is not configured")
        
        return service.get_playlist_tracks(playlist_id, **kwargs)
    
    def search_playlists(self, service_type: ServiceType, query: str, limit: int = 20) -> List[PlaylistInfo]:
        """Search playlists in a specific service"""
        service = self.get_service(service_type)
        if not service:
            raise ValueError(f"Service {service_type.value} is not configured")
        
        return service.search_playlists(query, limit)
    
    def get_supported_playlist_types(self, service_type: ServiceType) -> List[PlaylistType]:
        """Get supported playlist types for a service"""
        service = self.get_service(service_type)
        if not service:
            return []
        
        return service.get_supported_playlist_types()
    
    def test_service_connection(self, service_type: ServiceType) -> bool:
        """Test connection to a specific service"""
        service = self.get_service(service_type)
        if not service:
            return False
        
        return service.test_connection()
    
    def get_all_playlists(self, **kwargs) -> Dict[ServiceType, List[PlaylistInfo]]:
        """Get playlists from all enabled services"""
        results = {}
        
        for service_type in self.get_enabled_services():
            try:
                playlists = self.get_user_playlists(service_type, **kwargs)
                results[service_type] = playlists
            except Exception as e:
                print(f"Failed to get playlists from {service_type.value}: {e}")
                results[service_type] = []
        
        return results
    
    def search_all_services(self, query: str, limit: int = 20) -> Dict[ServiceType, List[PlaylistInfo]]:
        """Search playlists across all enabled services"""
        results = {}
        
        for service_type in self.get_enabled_services():
            try:
                playlists = self.search_playlists(service_type, query, limit)
                results[service_type] = playlists
            except Exception as e:
                print(f"Failed to search {service_type.value}: {e}")
                results[service_type] = []
        
        return results
    
    def debug_service_status(self) -> Dict[ServiceType, Dict[str, Any]]:
        """Get debug information about service status"""
        debug_info = {}
        
        for service_type in [ServiceType.LASTFM, ServiceType.YOUTUBE, ServiceType.SPOTIFY]:
            config = self.service_configs[service_type]
            service = self.services.get(service_type)
            
            debug_info[service_type] = {
                "enabled": config.enabled,
                "has_credentials": bool(config.credentials),
                "credentials_keys": list(config.credentials.keys()) if config.credentials else [],
                "service_instance": service is not None,
                "connection_test": service.test_connection() if service else False
            }
        
        return debug_info
