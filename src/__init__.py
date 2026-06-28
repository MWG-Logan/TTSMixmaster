"""TTSMixmaster package initialization"""

from importlib.metadata import version, PackageNotFoundError

try:
    # Single source of truth: the version declared in pyproject.toml, read from
    # the installed distribution metadata (e.g. after `pip install .`).
    __version__ = version("ttsmixmaster")
except PackageNotFoundError:
    try:
        # Frozen builds (PyInstaller) have no distribution metadata; CI writes a
        # generated _version.py derived from pyproject.toml at build time.
        from ._version import __version__
    except ImportError:
        __version__ = "0.0.0+dev"

__author__ = "TTSMixmaster Developer"
__description__ = "A tool for managing Last.fm playlists and integrating them with Tabletop Simulator"
