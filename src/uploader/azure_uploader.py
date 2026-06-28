"""
Azure Blob Storage Uploader Module

This module provides functionality to upload audio files to Azure Blob Storage
for use with Tabletop Simulator. Files uploaded here get public URLs that
TTS can access directly.
"""
import os
import mimetypes
from pathlib import Path
from typing import Optional, Dict, Any, List
from dataclasses import dataclass
import logging
from datetime import datetime, timedelta, timezone

try:
    from azure.storage.blob import BlobServiceClient, ContainerClient, PublicAccess
    from azure.storage.blob import ContentSettings
    from azure.core.exceptions import AzureError, ResourceNotFoundError
    AZURE_AVAILABLE = True
except ImportError:
    AZURE_AVAILABLE = False
    ContentSettings = None
    class AzureError(Exception): pass
    class ResourceNotFoundError(AzureError): pass
    logging.warning("Azure SDK not available. Please install azure-storage-blob to enable uploads.")

from ..api.lastfm_client import Track


@dataclass
class UploadResult:
    """Represents the result of an upload operation"""
    success: bool
    file_path: str
    public_url: Optional[str] = None
    blob_name: Optional[str] = None
    file_size: int = 0
    upload_time: Optional[datetime] = None
    error_message: Optional[str] = None
    metadata: Optional[Dict[str, str]] = None
    skipped: bool = False  # True when an up-to-date blob was left untouched


class AzureBlobUploader:
    """Uploads audio files to Azure Blob Storage for TTS integration"""
    
    def __init__(self, connection_string: Optional[str] = None, account_name: Optional[str] = None, 
                 account_key: Optional[str] = None, container_name: str = "tts-audio",
                 freshness_days: float = 1.0):
        """
        Initialize the Azure Blob uploader
        
        Args:
            connection_string: Azure Storage connection string
            account_name: Azure Storage account name (if not using connection string)
            account_key: Azure Storage account key (if not using connection string)
            container_name: Name of the container to upload to
            freshness_days: Skip re-uploading a blob when it was last modified
                within this many days. Kept short by default so most files get
                replaced; the blob name/URL is always preserved on replacement.
        """
        self.container_name = container_name
        self.freshness_days = freshness_days
        self.logger = logging.getLogger(__name__)
        self.blob_service_client = None
        self.account_name = account_name or "unknown"
        
        if not AZURE_AVAILABLE:
            self.logger.error("Azure SDK not available. Install azure-storage-blob to use this uploader.")
            return
            
        try:
            if connection_string:
                self.blob_service_client = BlobServiceClient.from_connection_string(connection_string)  # type: ignore
                # Extract account name from connection string if possible
                if "AccountName=" in connection_string:
                    self.account_name = connection_string.split("AccountName=")[1].split(";")[0]
            elif account_name and account_key:
                account_url = f"https://{account_name}.blob.core.windows.net"
                self.blob_service_client = BlobServiceClient(account_url=account_url, credential=account_key)  # type: ignore
                self.account_name = account_name
            else:
                # Try to get from environment variables
                env_connection_string = os.getenv('AZURE_STORAGE_CONNECTION_STRING')
                if env_connection_string:
                    self.blob_service_client = BlobServiceClient.from_connection_string(env_connection_string)  # type: ignore
                    if "AccountName=" in env_connection_string:
                        self.account_name = env_connection_string.split("AccountName=")[1].split(";")[0]
                else:
                    self.logger.error("No Azure Storage credentials provided")
                    return
            
            # Ensure container exists
            self._ensure_container_exists()
            
        except Exception as e:
            self.logger.error(f"Failed to initialize Azure Blob client: {e}")
            self.blob_service_client = None
    
    def _ensure_container_exists(self):
        """Ensure the container exists and is configured for public access"""
        if not self.blob_service_client:
            return
            
        try:
            container_client = self.blob_service_client.get_container_client(self.container_name)  # type: ignore
            
            # Try to get container properties to see if it exists
            try:
                container_client.get_container_properties()  # type: ignore
                self.logger.info(f"Container '{self.container_name}' already exists")
            except Exception:
                # Container doesn't exist, create it with public blob access
                self.logger.info(f"Creating container '{self.container_name}' with public blob access")
                if AZURE_AVAILABLE:
                    container_client.create_container(public_access=PublicAccess.Blob)  # type: ignore
                
        except Exception as e:
            self.logger.error(f"Failed to ensure container exists: {e}")
            raise
    
    def upload_audio_file(self, file_path: str, track: Optional[Track] = None, 
                         custom_blob_name: Optional[str] = None,
                         force: bool = False) -> UploadResult:
        """
        Upload an audio file to Azure Blob Storage
        
        Args:
            file_path: Path to the audio file to upload
            track: Track object for metadata (optional)
            custom_blob_name: Custom blob name (optional, will generate if not provided)
            force: Upload even if an up-to-date blob already exists
            
        Returns:
            UploadResult object
        """
        if not self.blob_service_client:
            return UploadResult(
                success=False,
                file_path=file_path,
                error_message="Azure Blob client not initialized"
            )        
        file_path_obj = Path(file_path)
        if not file_path_obj.exists():
            return UploadResult(
                success=False,
                file_path=file_path,
                error_message="File does not exist"
            )
        
        try:
            # Generate blob name (stable, no hash - preserves the standing URL)
            blob_name = custom_blob_name or self._generate_blob_name(file_path_obj, track)
            
            blob_client = self.blob_service_client.get_blob_client(  # type: ignore
                container=self.container_name, 
                blob=blob_name
            )
            
            public_url = f"https://{self.account_name}.blob.core.windows.net/{self.container_name}/{blob_name}"
            
            # Delta check: only skip an existing blob when it was modified
            # recently enough to be considered up to date. Older (stale) blobs
            # are replaced in place, keeping the same blob name and public URL.
            if not force:
                is_fresh, last_modified = self._blob_is_fresh(blob_client)
                if is_fresh:
                    self.logger.info(
                        f"Skipping up-to-date blob (modified {last_modified}): {blob_name}"
                    )
                    return UploadResult(
                        success=True,
                        file_path=str(file_path_obj),
                        public_url=public_url,
                        blob_name=blob_name,
                        file_size=file_path_obj.stat().st_size,
                        upload_time=last_modified,
                        metadata=self._prepare_metadata(file_path_obj, track),
                        skipped=True
                    )
            
            # Prepare metadata
            metadata = self._prepare_metadata(file_path_obj, track)
            
            # Get file info
            file_size = file_path_obj.stat().st_size
            content_type = mimetypes.guess_type(str(file_path_obj))[0] or 'audio/mpeg'
            
            self.logger.info(f"Uploading {file_path_obj.name} to Azure Blob Storage...")
            
            with open(file_path_obj, 'rb') as data:
                # Create ContentSettings object if Azure is available
                content_settings = None
                if AZURE_AVAILABLE and ContentSettings is not None:
                    try:
                        content_settings = ContentSettings(
                            content_type=content_type,
                            content_disposition=f'inline; filename="{file_path_obj.name}"'
                        )
                    except Exception as e:
                        self.logger.warning(f"Could not create ContentSettings: {e}")
                        content_settings = None
                
                blob_client.upload_blob(  # type: ignore
                    data, 
                    overwrite=True,
                    content_settings=content_settings,
                    metadata=metadata
                )
            
            self.logger.info(f"Successfully uploaded to: {public_url}")
            
            return UploadResult(
                success=True,
                file_path=file_path,
                public_url=public_url,
                blob_name=blob_name,
                file_size=file_size,
                upload_time=datetime.now(timezone.utc),
                metadata=metadata
            )
            
        except Exception as e:
            if AZURE_AVAILABLE:
                try:
                    from azure.core.exceptions import AzureError
                    if isinstance(e, AzureError):
                        error_msg = f"Azure upload failed: {str(e)}"
                    else:
                        error_msg = f"Upload failed: {str(e)}"
                except ImportError:
                    error_msg = f"Upload failed: {str(e)}"
            else:
                error_msg = f"Upload failed: {str(e)}"
                
            self.logger.error(error_msg)
            return UploadResult(
                success=False,
                file_path=file_path,                error_message=error_msg
            )
    
    def _blob_is_fresh(self, blob_client) -> tuple:
        """
        Determine whether an existing blob is recent enough to skip re-uploading.

        A blob is "fresh" when it exists and was last modified within
        ``self.freshness_days``. Missing or older blobs are treated as stale so
        they get replaced.

        Args:
            blob_client: BlobClient for the target blob

        Returns:
            Tuple of (is_fresh, last_modified). last_modified is None when the
            blob does not exist.
        """
        # A non-positive window means "always replace".
        if self.freshness_days is None or self.freshness_days <= 0:
            return False, None

        try:
            props = blob_client.get_blob_properties()  # type: ignore
        except ResourceNotFoundError:
            return False, None
        except Exception as e:
            # If properties can't be read, fall back to uploading.
            self.logger.warning(f"Could not read blob properties, will upload: {e}")
            return False, None

        last_modified = getattr(props, 'last_modified', None)
        if last_modified is None:
            return False, None

        # Normalize to an aware UTC datetime for comparison.
        if last_modified.tzinfo is None:
            last_modified = last_modified.replace(tzinfo=timezone.utc)

        cutoff = datetime.now(timezone.utc) - timedelta(days=self.freshness_days)
        return last_modified >= cutoff, last_modified

    def _generate_blob_name(self, file_path: Path, track: Optional[Track] = None) -> str:
        """
        Generate a stable blob name for the file.

        The name is derived from the on-disk filename (spaces preserved) so that
        re-uploading the same track keeps the exact same blob path, and therefore
        the same public URL (e.g. ``audio/Staind - For You.mp3``). No hash is
        appended because the URL must stay stable across replacements.

        Args:
            file_path: Path to the file
            track: Track object (unused for naming; kept for API compatibility)

        Returns:
            Generated blob name
        """
        return f"audio/{file_path.name}"
    
    def _prepare_metadata(self, file_path: Path, track: Optional[Track] = None) -> Dict[str, str]:
        """Prepare metadata for the blob"""
        metadata = {
            'source': 'TTSMixmaster',
            'upload_time': datetime.utcnow().isoformat(),
            'original_filename': file_path.name,
            'file_size': str(file_path.stat().st_size)
        }
        
        if track:
            metadata.update({
                'artist': track.artist or '',
                'title': track.title or '',
                'album': track.album or '',
                'duration': str(track.duration) if track.duration else '0'
            })
        
        # Azure metadata keys must be valid
        return {k: v for k, v in metadata.items() if v and k.replace('_', '').isalnum()}
    
    def upload_playlist_files(self, file_paths: List[str], tracks: Optional[List[Track]] = None) -> List[UploadResult]:
        """
        Upload multiple files for a playlist
        
        Args:
            file_paths: List of file paths to upload
            tracks: List of corresponding Track objects (optional)
            
        Returns:
            List of UploadResult objects
        """
        results = []
        tracks_list = tracks or [None] * len(file_paths)
        
        for i, file_path in enumerate(file_paths):
            track = tracks_list[i] if i < len(tracks_list) else None
            result = self.upload_audio_file(file_path, track)
            results.append(result)
            
            if result.success:
                self.logger.info(f"Uploaded {i+1}/{len(file_paths)}: {Path(file_path).name}")
            else:
                self.logger.error(f"Failed to upload {i+1}/{len(file_paths)}: {result.error_message}")
        
        return results
    
    def list_uploaded_files(self, prefix: str = "audio/") -> List[Dict[str, Any]]:
        """
        List files in the container
        
        Args:
            prefix: Blob name prefix to filter by
            
        Returns:
            List of blob information dictionaries
        """
        if not self.blob_service_client:
            return []
        
        try:
            container_client = self.blob_service_client.get_container_client(self.container_name)  # type: ignore
            blobs = []
            
            for blob in container_client.list_blobs(name_starts_with=prefix):  # type: ignore
                blob_info = {
                    'name': blob.name,
                    'size': blob.size,
                    'last_modified': blob.last_modified,
                    'url': f"https://{self.account_name}.blob.core.windows.net/{self.container_name}/{blob.name}",
                    'metadata': blob.metadata or {}
                }
                blobs.append(blob_info)
            
            return blobs
            
        except Exception as e:
            self.logger.error(f"Failed to list blobs: {e}")
            return []
    
    def delete_file(self, blob_name: str) -> bool:
        """
        Delete a file from Azure Blob Storage
        
        Args:
            blob_name: Name of the blob to delete
            
        Returns:
            True if successful, False otherwise
        """
        if not self.blob_service_client:
            return False
        
        try:
            blob_client = self.blob_service_client.get_blob_client(  # type: ignore
                container=self.container_name,
                blob=blob_name
            )
            blob_client.delete_blob()  # type: ignore
            self.logger.info(f"Deleted blob: {blob_name}")
            return True
            
        except Exception as e:
            self.logger.error(f"Failed to delete blob {blob_name}: {e}")
            return False
    
    def get_upload_stats(self) -> Dict[str, Any]:
        """Get statistics about uploaded files"""
        files = self.list_uploaded_files()
        
        if not files:
            return {
                'total_files': 0,
                'total_size': 0,
                'last_upload': None
            }
        
        total_size = sum(f['size'] for f in files)
        last_upload = max(f['last_modified'] for f in files) if files else None
        
        return {
            'total_files': len(files),
            'total_size': total_size,
            'total_size_mb': round(total_size / (1024 * 1024), 2),
            'last_upload': last_upload,
            'container_name': self.container_name
        }
