"""Run with the main checkout's .venv/Scripts/python.exe -I <this script>.

-I ignores PYTHONPATH, so explicitly add the worktree root from this script's location.
No legacy Lua is executed. All generated files are inert reference text.
Only random object GUIDs are fixed; Python generates no timestamps in these outputs.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT))

from src.api.base_service import Track, PlaylistInfo
from src.uploader.azure_uploader import UploadResult
from src.tts_formatter.tts_formatter import TTSFormatter

CASES = [
    {"id": "ordinary", "name": "Evening Mix", "tracks": [
        {"artist": "Artist One", "title": "Opening", "album": "First", "url": "https://example.com/audio/one.mp3"},
        {"artist": "Artist Two", "title": "Missing", "album": "", "url": ""}], "options": {}},
    {"id": "escaping", "name": 'Café "mix"\\night\n次', "tracks": [
        {"artist": 'Björk "A"', "title": 'Back\\slash\nNew\rLine\tTab 🎵', "album": '日本 "盤"', "url": 'https://example.com/a%20b.mp3?x="q"&y=\\z'},
        {"artist": "Same", "title": "Title", "album": "", "url": "https://example.com/other.mp3"},
        {"artist": "Same", "title": "Title", "album": "Other", "url": ""}],
     "options": {"nickname": 'Custom "name"\\\n音', "description": "Description\nsecond\tline", "image_url": " https://example.com/front.png ", "image_secondary_url": " https://example.com/back.png "}},
    {"id": "empty", "name": "Empty Collection", "tracks": [], "options": {}}
]


def generate():
    output = Path(__file__).resolve().parent
    formatter = TTSFormatter(str(output))
    (output / "cases.json").write_text(json.dumps(CASES, indent=2, ensure_ascii=False), encoding="utf-8", newline="\n")
    for case in CASES:
        tracks = [Track(artist=t["artist"], title=t["title"], album=t["album"]) for t in case["tracks"]]
        uploads = [UploadResult(success=bool(t["url"]), file_path="", public_url=t["url"]) for t in case["tracks"]]
        player = formatter.create_music_player_from_playlist_info(PlaylistInfo(name=case["name"], tracks=tracks), uploads)
        options = case["options"].copy()
        # Mirror the main_window default nickname, not just the formatter's lower-level default.
        options.setdefault("nickname", case["name"] + " Music Player")
        artifacts = {
            "player.lua": formatter.generate_lua_script(player),
            "playlist.lua": formatter.generate_simple_playlist_lua(player),
            "data.json": formatter.generate_json_data(player),
            "save.json": json.dumps(formatter.generate_save_file(player, object_guid="ABC123", **options), indent=2, ensure_ascii=False),
            "full-save.json": json.dumps(formatter.generate_save_file(player, object_guid="ABC123", use_simple_format=False, **options), indent=2, ensure_ascii=False),
        }
        for suffix, text in artifacts.items():
            (output / (case["id"] + "." + suffix)).write_text(text, encoding="utf-8", newline="\n")
    print(f"Generated {len(CASES)} cases, {len(CASES) * len(artifacts)} unchanged Python artifacts")


if __name__ == "__main__":
    generate()
