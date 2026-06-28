# -*- mode: python ; coding: utf-8 -*-
import os
import sys
from pathlib import Path

# Get the current directory
current_dir = Path('.').resolve()

block_cipher = None

# Exclude unnecessary modules to reduce size
excludes = [
    'tkinter.test',
    'unittest',
    'email',
    'http',
    'urllib',
    'xml',
    'xmlrpc',
    'pydoc',
    'doctest',
    'zipfile',
    'tarfile',
    'calendar',
    'pdb',
    'inspect',
    'dis',
    'pickle',
    'subprocess',
    'multiprocessing'
]

a = Analysis(
    ['main.py'],
    pathex=[str(current_dir)],
    binaries=[],
    datas=[
        ('config.json.template', '.'),  # Include template, not actual config
    ],
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