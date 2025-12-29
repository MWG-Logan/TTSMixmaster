"""
New Playlist Tab Module

This module provides the updated playlist management interface
that supports multiple music services.
"""

import tkinter as tk
from tkinter import ttk, messagebox
import customtkinter as ctk
from typing import List, Optional, Dict, Any
import threading

from ..api.service_manager import MusicServiceManager, ServiceType
from ..api.base_service import PlaylistInfo, PlaylistType


class PlaylistTabManager:
    """Manages the new playlist tab with multi-service support"""
    
    def __init__(self, parent_tab, config_manager, main_app):
        self.parent_tab = parent_tab
        self.config_manager = config_manager
        self.main_app = main_app
        self.service_manager = MusicServiceManager()
        
        # Current data
        self.current_playlists: List[PlaylistInfo] = []
        self.selected_service: Optional[ServiceType] = None
        self.selected_playlist: Optional[PlaylistInfo] = None
        
        # Setup UI
        self._setup_ui()
        self._initialize_services()
    
    def _setup_ui(self):
        """Setup the playlist tab UI"""
        # Main container
        main_frame = ctk.CTkFrame(self.parent_tab)
        main_frame.pack(fill="both", expand=True, padx=10, pady=10)
        
        # Title
        title_label = ctk.CTkLabel(
            main_frame, 
            text="Music Collections & Playlists", 
            font=ctk.CTkFont(size=20, weight="bold")
        )
        title_label.pack(pady=(10, 20))
          # Service selection frame
        self._create_service_selection_frame(main_frame)
        
        # Control buttons frame
        self._create_controls_frame(main_frame)
        
        # Results display frame
        self._create_results_frame(main_frame)
        
        # Status frame
        self._create_status_frame(main_frame)
    
    def _create_service_selection_frame(self, parent):
        """Create service selection interface"""
        service_frame = ctk.CTkFrame(parent)
        service_frame.pack(fill="x", padx=10, pady=5)
        
        # Service selection
        service_select_frame = ctk.CTkFrame(service_frame)
        service_select_frame.pack(fill="x", padx=10, pady=10)
        
        ctk.CTkLabel(service_select_frame, text="Music Service:", font=ctk.CTkFont(weight="bold")).pack(side="left", padx=5)
        
        self.service_var = ctk.StringVar(value="lastfm")
        service_options = [
            ("Last.fm Collections", "lastfm"),
            ("YouTube Playlists", "youtube"), 
            ("Spotify Playlists", "spotify")
        ]
        
        self.service_radio_buttons = {}
        for text, value in service_options:
            radio = ctk.CTkRadioButton(
                service_select_frame,
                text=text,
                variable=self.service_var,
                value=value,
                command=self._on_service_changed
            )
            radio.pack(side="left", padx=10)
            self.service_radio_buttons[value] = radio
        
        # Service-specific options frame
        self.options_frame = ctk.CTkFrame(service_frame)
        self.options_frame.pack(fill="x", padx=10, pady=5)
        
        # Create option panels for each service (initially hidden)
        self._create_lastfm_options()
        self._create_youtube_options()
        self._create_spotify_options()
        
        # Show initial service options
        self._on_service_changed()
    
    def _create_lastfm_options(self):
        """Create Last.fm specific options"""
        self.lastfm_options = ctk.CTkFrame(self.options_frame)
        
        # Collection type selection
        type_frame = ctk.CTkFrame(self.lastfm_options)
        type_frame.pack(fill="x", padx=5, pady=5)
        
        ctk.CTkLabel(type_frame, text="Collection Type:").pack(side="left", padx=5)
        self.lastfm_type_var = ctk.StringVar(value="top_tracks")
        lastfm_types = [
            ("Top Tracks", "top_tracks"),
            ("Loved Tracks", "loved_tracks"),
            ("Recent Tracks", "recent_tracks")
        ]
        
        for text, value in lastfm_types:
            radio = ctk.CTkRadioButton(type_frame, text=text, variable=self.lastfm_type_var, value=value)
            radio.pack(side="left", padx=10)
        
        # Period and limit for top tracks
        params_frame = ctk.CTkFrame(self.lastfm_options)
        params_frame.pack(fill="x", padx=5, pady=5)
        
        ctk.CTkLabel(params_frame, text="Time Period:").pack(side="left", padx=5)
        self.lastfm_period_var = ctk.StringVar(value="overall")
        periods = ["overall", "7day", "1month", "3month", "6month", "12month"]
        self.lastfm_period_combo = ctk.CTkComboBox(params_frame, values=periods, variable=self.lastfm_period_var, width=120)
        self.lastfm_period_combo.pack(side="left", padx=5)
        
        ctk.CTkLabel(params_frame, text="Limit:").pack(side="left", padx=(20, 5))
        self.lastfm_limit_var = ctk.StringVar(value="50")
        self.lastfm_limit_entry = ctk.CTkEntry(params_frame, width=80, textvariable=self.lastfm_limit_var)
        self.lastfm_limit_entry.pack(side="left", padx=5)
    
    def _create_youtube_options(self):
        """Create YouTube specific options"""
        self.youtube_options = ctk.CTkFrame(self.options_frame)
        
        # Action type selection
        action_frame = ctk.CTkFrame(self.youtube_options)
        action_frame.pack(fill="x", padx=5, pady=5)
        
        ctk.CTkLabel(action_frame, text="Action:").pack(side="left", padx=5)
        self.youtube_action_var = ctk.StringVar(value="my_playlists")
        youtube_actions = [
            ("My Playlists", "my_playlists"),
            ("Search Playlists", "search")
        ]
        
        for text, value in youtube_actions:
            radio = ctk.CTkRadioButton(action_frame, text=text, variable=self.youtube_action_var, value=value)
            radio.pack(side="left", padx=10)
        
        # Search query frame
        search_frame = ctk.CTkFrame(self.youtube_options)
        search_frame.pack(fill="x", padx=5, pady=5)
        
        ctk.CTkLabel(search_frame, text="Search Query:").pack(side="left", padx=5)
        self.youtube_search_var = ctk.StringVar()
        self.youtube_search_entry = ctk.CTkEntry(search_frame, textvariable=self.youtube_search_var, width=300)
        self.youtube_search_entry.pack(side="left", padx=5)
        
        ctk.CTkLabel(search_frame, text="Limit:").pack(side="left", padx=(20, 5))
        self.youtube_limit_var = ctk.StringVar(value="20")
        self.youtube_limit_entry = ctk.CTkEntry(search_frame, width=80, textvariable=self.youtube_limit_var)
        self.youtube_limit_entry.pack(side="left", padx=5)
    
    def _create_spotify_options(self):
        """Create Spotify specific options"""
        self.spotify_options = ctk.CTkFrame(self.options_frame)
        
        # Action type selection
        action_frame = ctk.CTkFrame(self.spotify_options)
        action_frame.pack(fill="x", padx=5, pady=5)
        
        ctk.CTkLabel(action_frame, text="Action:").pack(side="left", padx=5)
        self.spotify_action_var = ctk.StringVar(value="my_playlists")
        spotify_actions = [
            ("My Playlists", "my_playlists"),
            ("Search Playlists", "search")
        ]
        
        for text, value in spotify_actions:
            radio = ctk.CTkRadioButton(action_frame, text=text, variable=self.spotify_action_var, value=value)
            radio.pack(side="left", padx=10)        # User ID and search frame
        params_frame = ctk.CTkFrame(self.spotify_options)
        params_frame.pack(fill="x", padx=5, pady=5)
        
        ctk.CTkLabel(params_frame, text="User ID (optional):").pack(side="left", padx=5)
        self.spotify_user_var = ctk.StringVar()
        self.spotify_user_entry = ctk.CTkEntry(params_frame, textvariable=self.spotify_user_var, width=150)
        self.spotify_user_entry.pack(side="left", padx=5)
        
        ctk.CTkLabel(params_frame, text="Search/Limit:").pack(side="left", padx=(20, 5))
        self.spotify_search_var = ctk.StringVar()
        self.spotify_search_entry = ctk.CTkEntry(params_frame, textvariable=self.spotify_search_var, width=200)
        self.spotify_search_entry.pack(side="left", padx=5)
    
    def _create_controls_frame(self, parent):
        """Create control buttons"""
        controls_frame = ctk.CTkFrame(parent)
        controls_frame.pack(fill="x", padx=10, pady=5)
        
        button_frame = ctk.CTkFrame(controls_frame)
        button_frame.pack(pady=10)
        
        # Fetch button
        self.fetch_button = ctk.CTkButton(
            button_frame,
            text="Fetch Collections/Playlists",
            command=self._fetch_playlists,
            width=200
        )
        self.fetch_button.pack(side="left", padx=5)
        
        # Clear button
        self.clear_button = ctk.CTkButton(
            button_frame,
            text="Clear",
            command=self._clear_results,
            width=80
        )
        self.clear_button.pack(side="left", padx=5)
        
        # Debug button (can be removed later)
        debug_button = ctk.CTkButton(
            button_frame,
            text="Debug Services",
            command=self._debug_services,
            width=120
        )
        debug_button.pack(side="left", padx=5)
        
        # Playlist selection frame
        selection_frame = ctk.CTkFrame(controls_frame)
        selection_frame.pack(fill="x", padx=10, pady=5)
        
        ctk.CTkLabel(selection_frame, text="Select Playlist:", font=ctk.CTkFont(weight="bold")).pack(side="left", padx=5)
        
        self.playlist_var = ctk.StringVar()
        self.playlist_dropdown = ctk.CTkComboBox(
            selection_frame,
            values=["No playlists available"],
            variable=self.playlist_var,
            command=self._on_playlist_selected,
            width=400,
            state="disabled"
        )
        self.playlist_dropdown.pack(side="left", padx=10)
        
        # Load tracks button
        self.load_tracks_button = ctk.CTkButton(
            selection_frame,
            text="Load Tracks",
            command=self._load_tracks,
            width=120,
            state="disabled"
        )
        self.load_tracks_button.pack(side="left", padx=5)        # Service status indicators
        status_frame = ctk.CTkFrame(controls_frame)
        status_frame.pack(fill="x", padx=10, pady=5)
        
        ctk.CTkLabel(status_frame, text="Service Status:", font=ctk.CTkFont(weight="bold")).pack(side="left", padx=5)
        
        self.status_labels = {}
        for service in ["lastfm", "youtube", "spotify"]:
            label = ctk.CTkLabel(status_frame, text=f"{service.title()}: ❌", text_color="red")
            label.pack(side="left", padx=10)
            self.status_labels[service] = label

    def _create_results_frame(self, parent):
        """Create results display with tracks table"""
        results_frame = ctk.CTkFrame(parent)
        results_frame.pack(fill="both", expand=True, padx=10, pady=5)
        
        ctk.CTkLabel(results_frame, text="Tracks", font=ctk.CTkFont(size=16, weight="bold")).pack(pady=5)
        
        # Create tracks table directly (no tabs needed)
        self._create_tracks_table(results_frame)
    
    def _create_status_frame(self, parent):
        """Create status display"""
        status_frame = ctk.CTkFrame(parent)
        status_frame.pack(fill="x", padx=10, pady=5)
        
        self.status_label = ctk.CTkLabel(status_frame, text="Ready")
        self.status_label.pack(pady=5)
    
    def _create_tracks_table(self, parent):
        """Create a table for displaying tracks and artists"""
        self.tracks_frame = ctk.CTkFrame(parent)
        self.tracks_frame.pack(fill="both", expand=True, padx=10, pady=5)

        self.tracks_table = ttk.Treeview(self.tracks_frame, columns=("#", "Artist", "Title"), show="headings", height=12)
        self.tracks_table.heading("#", text="#")
        self.tracks_table.heading("Artist", text="Artist")
        self.tracks_table.heading("Title", text="Title")
        self.tracks_table.column("#", width=40, anchor="center")
        self.tracks_table.column("Artist", width=200)
        self.tracks_table.column("Title", width=300)
        self.tracks_table.pack(fill="both", expand=True)

    def _populate_playlist_dropdown(self):
        """Populate the playlist dropdown with current playlists"""
        names = [pl.name for pl in self.current_playlists] if self.current_playlists else ["No playlists available"]
        self.playlist_dropdown.configure(values=names)
        self.playlist_dropdown.configure(state="normal" if self.current_playlists else "disabled")
        if self.current_playlists:
            self.playlist_var.set(names[0])
            self._on_playlist_selected(names[0])
        else:
            self.playlist_var.set("")
            self._clear_tracks_table()

    def _on_playlist_selected(self, selected_name=None):
        """Handler for when a playlist is selected from the dropdown"""
        if not self.current_playlists:
            self._clear_tracks_table()
            return
        name = selected_name or self.playlist_var.get()
        playlist = next((pl for pl in self.current_playlists if pl.name == name), None)
        self.selected_playlist = playlist
        if playlist:
            # Enable the load tracks button
            self.load_tracks_button.configure(state="normal")            # If playlist already has tracks, show them immediately
            if playlist.tracks:
                self._update_tracks_table(playlist)
            else:
                self._clear_tracks_table()
        else:
            self._clear_tracks_table()
            self.load_tracks_button.configure(state="disabled")

    def _update_tracks_table(self, playlist):
        """Update the tracks table with tracks from the selected playlist"""
        self._clear_tracks_table()
        if not playlist or not playlist.tracks:
            return
        for idx, track in enumerate(playlist.tracks, 1):
            self.tracks_table.insert("", "end", values=(idx, getattr(track, "artist", ""), getattr(track, "title", "")))

    def _clear_tracks_table(self):
        """Clear all rows from the tracks table"""
        if hasattr(self, "tracks_table"):
            for row in self.tracks_table.get_children():
                self.tracks_table.delete(row)

    def _on_service_changed(self):
        """Handle service selection change"""
        # Hide all option frames
        for frame in [self.lastfm_options, self.youtube_options, self.spotify_options]:
            frame.pack_forget()
        
        # Show selected service options
        service = self.service_var.get()
        if service == "lastfm":
            self.lastfm_options.pack(fill="x", padx=5, pady=5)
        elif service == "youtube":
            self.youtube_options.pack(fill="x", padx=5, pady=5)
        elif service == "spotify":
            self.spotify_options.pack(fill="x", padx=5, pady=5)
    
    def _initialize_services(self):
        """Initialize services with configuration"""
        config = self.config_manager.get_config()
          # Initialize Last.fm
        if config.lastfm_api_key and config.lastfm_username:
            lastfm_creds = {
                'api_key': config.lastfm_api_key,
                'api_secret': config.lastfm_api_secret,
                'username': config.lastfm_username
            }
            if self.service_manager.configure_service(ServiceType.LASTFM, lastfm_creds):
                self.status_labels["lastfm"].configure(text="Last.fm: ✅", text_color="green")
        
        # Initialize YouTube
        if config.youtube_api_key:
            youtube_creds = {
                'api_key': config.youtube_api_key,
                'channel_id': config.youtube_channel_id
            }
            if self.service_manager.configure_service(ServiceType.YOUTUBE, youtube_creds):
                self.status_labels["youtube"].configure(text="YouTube: ✅", text_color="green")
        
        # Initialize Spotify
        if config.spotify_client_id and config.spotify_client_secret:
            spotify_creds = {
                'client_id': config.spotify_client_id,
                'client_secret': config.spotify_client_secret,
                'user_id': config.spotify_user_id,
                'redirect_uri': config.spotify_redirect_uri,
                'use_oauth': config.spotify_use_oauth
            }
            if self.service_manager.configure_service(ServiceType.SPOTIFY, spotify_creds):
                self.status_labels["spotify"].configure(text="Spotify: ✅", text_color="green")
    
    def reinitialize_services(self):
        """Reinitialize services with updated configuration"""
        self._initialize_services()
        
        # Update status display to reflect current service states
        enabled_services = self.service_manager.get_enabled_services()
        
        # Reset all status labels first
        for service_key in self.status_labels:
            service_name = service_key.title()
            if service_key == "lastfm":
                service_name = "Last.fm"
            self.status_labels[service_key].configure(text=f"{service_name}: ❌", text_color="red")
        
        # Update enabled services
        for service_type in enabled_services:
            service_key = service_type.value
            service_name = service_key.title()
            if service_key == "lastfm":
                service_name = "Last.fm"
            self.status_labels[service_key].configure(text=f"{service_name}: ✅", text_color="green")
    
    def _fetch_playlists(self):
        """Fetch playlists from selected service"""
        def fetch_worker():
            try:
                service_str = self.service_var.get()
                service_type = ServiceType(service_str)
                
                if not self.service_manager.is_service_enabled(service_type):
                    messagebox.showerror("Error", f"{service_str.title()} service is not configured or enabled")
                    return
                
                self._update_status("Fetching playlists...")
                
                if service_type == ServiceType.LASTFM:
                    self._fetch_lastfm_collections()
                elif service_type == ServiceType.YOUTUBE:
                    self._fetch_youtube_playlists()
                elif service_type == ServiceType.SPOTIFY:
                    self._fetch_spotify_playlists()                
                self._populate_playlist_dropdown()
                self._update_status(f"Fetched {len(self.current_playlists)} playlists from {service_str.title()}")
                
            except Exception as e:
                messagebox.showerror("Error", f"Failed to fetch playlists: {e}")
                self._update_status("Failed to fetch playlists")
        
        threading.Thread(target=fetch_worker, daemon=True).start()
    
    def _fetch_lastfm_collections(self):
        """Fetch Last.fm collections"""
        collection_type = self.lastfm_type_var.get()
        limit = int(self.lastfm_limit_var.get())
        period = self.lastfm_period_var.get()
        
        if collection_type == "top_tracks":
            playlist = self.service_manager.get_playlist_tracks(
                ServiceType.LASTFM, "top_tracks", 
                period=period, limit=limit
            )
        elif collection_type == "loved_tracks":
            playlist = self.service_manager.get_playlist_tracks(
                ServiceType.LASTFM, "loved_tracks", 
                limit=limit
            )
        else:  # recent_tracks
            playlist = self.service_manager.get_playlist_tracks(
                ServiceType.LASTFM, "recent_tracks", 
                limit=limit
            )
        
        self.current_playlists = [playlist]
        self.selected_playlist = playlist
    
    def _fetch_youtube_playlists(self):
        """Fetch YouTube playlists"""
        action = self.youtube_action_var.get()
        
        if action == "my_playlists":
            playlists = self.service_manager.get_user_playlists(ServiceType.YOUTUBE)
        else:  # search
            query = self.youtube_search_var.get().strip()
            if not query:
                raise ValueError("Search query is required")
            
            limit = int(self.youtube_limit_var.get())
            playlists = self.service_manager.search_playlists(ServiceType.YOUTUBE, query, limit)
        
        self.current_playlists = playlists
        self.selected_playlist = None
    
    def _fetch_spotify_playlists(self):
        """Fetch Spotify playlists"""
        action = self.spotify_action_var.get()
        
        if action == "my_playlists":
            user_id = self.spotify_user_var.get().strip()
            kwargs = {}
            if user_id:
                kwargs['user_id'] = user_id
            
            playlists = self.service_manager.get_user_playlists(ServiceType.SPOTIFY, **kwargs)
        else:  # search
            query = self.spotify_search_var.get().strip()
            if not query:
                raise ValueError("Search query is required")
            playlists = self.service_manager.search_playlists(ServiceType.SPOTIFY, query, 20)
        
        self.current_playlists = playlists
        self.selected_playlist = None
    
    def _load_tracks(self):
        """Load tracks for selected playlist"""
        if not self.selected_playlist:
            messagebox.showwarning("Warning", "Please select a playlist first")
            return
            
        def load_worker():
            try:
                self._update_status("Loading tracks...")
                
                # Get the service type
                service_str = self.service_var.get()
                service_type = ServiceType(service_str)                # Load tracks for the selected playlist
                if self.selected_playlist and self.selected_playlist.service_id:
                    playlist_with_tracks = self.service_manager.get_playlist_tracks(
                        service_type, 
                        self.selected_playlist.service_id
                    )
                    
                    # Update the selected playlist with loaded tracks
                    self.selected_playlist.tracks = playlist_with_tracks.tracks
                    
                    # Update the tracks table
                    self._update_tracks_table(self.selected_playlist)
                    
                    self._update_status(f"Loaded {len(self.selected_playlist.tracks)} tracks")
                else:
                    raise ValueError("No playlist selected or playlist has no service ID")
                
            except Exception as e:
                messagebox.showerror("Error", f"Failed to load tracks: {e}")
                self._update_status("Failed to load tracks")        
        threading.Thread(target=load_worker, daemon=True).start()
        
    def _clear_results(self):
        """Clear all results"""
        self.current_playlists.clear()
        self.selected_playlist = None
        self._clear_tracks_table()
        self.playlist_dropdown.configure(values=["No playlists available"], state="disabled")
        self.playlist_var.set("")
        self.load_tracks_button.configure(state="disabled")
        self._update_status("Results cleared")
        
    def _update_status(self, message: str):
        """Update status label"""
        self.status_label.configure(text=message)
        
    def get_current_playlists(self) -> List[PlaylistInfo]:
        """Get current playlists for use by other components"""
        return self.current_playlists

    def get_selected_playlist(self) -> Optional[PlaylistInfo]:
        """Get the currently selected playlist"""
        return self.selected_playlist
    
    def get_selected_playlist_tracks(self) -> List:
        """Get tracks from the currently selected playlist"""
        if self.selected_playlist and self.selected_playlist.tracks:
            return self.selected_playlist.tracks
        return []

    def _debug_services(self):
        """Debugging function to show service configurations"""
        config = self.config_manager.get_config()
        
        debug_info = "=== Service Configurations ===\n\n"
        
        # Last.fm
        debug_info += "[Last.fm]\n"
        debug_info += f"API Key: {config.lastfm_api_key}\n"
        debug_info += f"API Secret: {config.lastfm_api_secret}\n"
        debug_info += f"Username: {config.lastfm_username}\n\n"
        
        # YouTube
        debug_info += "[YouTube]\n"
        debug_info += f"API Key: {config.youtube_api_key}\n"
        debug_info += f"Channel ID: {config.youtube_channel_id}\n\n"
        
        # Spotify
        debug_info += "[Spotify]\n"
        debug_info += f"Client ID: {config.spotify_client_id}\n"
        debug_info += f"Client Secret: {config.spotify_client_secret}\n"
        debug_info += f"User ID: {config.spotify_user_id}\n"
        
        # Show debug info in a message box instead
        messagebox.showinfo("Debug Info", debug_info)
        self._update_status("Debug info displayed")
