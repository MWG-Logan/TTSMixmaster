# -*- mode: python ; coding: utf-8 -*-
import os
import sys
from pathlib import Path
from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs

# Get the current directory
current_dir = Path('.').resolve()

block_cipher = None

# CustomTkinter ships theme/asset JSON files and imageio_ffmpeg bundles an
# ffmpeg binary; both must be collected explicitly or the app crashes at runtime.
ctk_datas = collect_data_files('customtkinter')
ffmpeg_datas = collect_data_files('imageio_ffmpeg')
ffmpeg_bins = collect_dynamic_libs('imageio_ffmpeg')

# Excluding stdlib modules here previously broke the build at runtime:
# urllib, http, email, xml, subprocess, pickle, inspect, etc. are required
# transitively by pathlib, requests, yt_dlp, spotipy and the Google/Azure SDKs.
# Keep this empty so PyInstaller bundles everything the dependencies need.
excludes = []

a = Analysis(
    ['main.py'],
    pathex=[str(current_dir)],
    binaries=ffmpeg_bins,
    datas=[
        ('config.json.template', '.'),  # Include template, not actual config
    ] + ctk_datas + ffmpeg_datas,
    hiddenimports=[
        'tkinter',
        'customtkinter',
        'PIL',
        'PIL._tkinter_finder',
        'requests',
        'pydub',
        'mutagen',
        'azure.storage.blob',
        'yt_dlp',
        'google.auth',
        'google.auth.transport.requests',
        'google.oauth2.credentials',
        'google.auth.exceptions',
        'googleapiclient.discovery',
        'googleapiclient.errors',
        'isodate',
        'spotipy',
        'configparser',
        'dotenv',
        'imageio_ffmpeg',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excludes,
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='TTSMixmaster',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,  # Set to False for GUI app
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,  # Add icon path if you have one
    onefile=True,  # This creates a single-file executable
    version_file=None,  # Consider adding version info
)