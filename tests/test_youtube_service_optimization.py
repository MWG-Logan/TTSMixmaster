from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, List

from src.api.youtube_service import YouTubeService
from src.api.base_service import PlaylistInfo


class _TestYouTubeService(YouTubeService):
    def search_playlists(self, query: str, limit: int = 20) -> List[PlaylistInfo]:
        return []


@dataclass
class _FakeRequest:
    response: Dict[str, Any]
    counter: Dict[str, int]
    name: str

    def execute(self) -> Dict[str, Any]:
        self.counter[self.name] = self.counter.get(self.name, 0) + 1
        return self.response


class _FakeYouTube:
    """Tiny fake YouTube client to count API calls made by YouTubeService."""

    def __init__(self, *, pages: List[Dict[str, Any]], video_details: Dict[str, Dict[str, Any]]):
        self._pages = pages
        self._video_details = video_details
        self._calls: Dict[str, int] = {}
        self._playlist_items_calls = 0

    def reset_playlist_items(self) -> None:
        self._playlist_items_calls = 0

    @property
    def calls(self) -> Dict[str, int]:
        return dict(self._calls)

    def playlists(self) -> "_FakeYouTube":
        return self

    def playlistItems(self) -> "_FakeYouTube":
        return self

    def videos(self) -> "_FakeYouTube":
        return self

    def search(self) -> "_FakeYouTube":
        return self

    def list(self, **kwargs: Any) -> _FakeRequest:  # pylint: disable=unused-argument
        # Determine which endpoint is being called by kwargs shape.
        if "playlistId" in kwargs:
            idx = self._playlist_items_calls
            self._playlist_items_calls += 1
            return _FakeRequest(self._pages[idx], self._calls, "playlistItems.list")

        part = str(kwargs.get("part", ""))

        # video details request (videos.list)
        if "contentDetails" in part and "statistics" in part and "id" in kwargs:
            ids = str(kwargs.get("id", "")).split(",") if kwargs.get("id") else []
            return _FakeRequest(
                {"items": [self._video_details[i] for i in ids if i in self._video_details]},
                self._calls,
                "videos.list",
            )

        # playlist metadata request (playlists.list)
        if "snippet" in part and "contentDetails" in part and "id" in kwargs and "playlistId" not in kwargs:
            return _FakeRequest(
                {
                    "items": [
                        {
                            "snippet": {
                                "title": "My Playlist",
                                "description": "",
                                "thumbnails": {},
                                "channelTitle": "Me",
                            },
                            "contentDetails": {"itemCount": 999},
                        }
                    ]
                },
                self._calls,
                "playlists.list",
            )

        # search request (search.list)
        if kwargs.get("type") == "video" and "q" in kwargs:
            return _FakeRequest({"items": []}, self._calls, "search.list")

        return _FakeRequest({"items": []}, self._calls, "unknown.list")


def _make_playlist_item(video_id: str, channel_title: str, title: str) -> Dict[str, Any]:
    return {
        "snippet": {
            "title": title,
            "channelTitle": channel_title,
            "videoOwnerChannelTitle": channel_title,
            "thumbnails": {},
        },
        "contentDetails": {"videoId": video_id},
    }


def test_topic_playlist_does_not_trigger_n_plus_one_searches() -> None:
    pages = [
        {
            "items": [_make_playlist_item(f"id{i}", "Some Artist - Topic", f"Some Artist - Song {i}") for i in range(50)],
            "nextPageToken": "NEXT",
        },
        {
            "items": [_make_playlist_item(f"id{i}", "Some Artist - Topic", f"Some Artist - Song {i}") for i in range(50, 100)],
        },
    ]
    video_details = {f"id{i}": {"id": f"id{i}", "contentDetails": {"duration": "PT2M"}} for i in range(100)}

    svc = _TestYouTubeService(api_key="")
    svc.youtube = _FakeYouTube(pages=pages, video_details=video_details)

    # No explicit max_results => should load full playlist (based on playlist itemCount)
    playlist = svc.get_playlist_tracks(
        "PL123",
        prefer_official=False,
    )

    assert len(playlist.tracks) == 100
    assert svc.youtube.calls.get("search.list", 0) == 0
    assert svc.youtube.calls.get("playlistItems.list", 0) == 2
    assert svc.youtube.calls.get("videos.list", 0) == 2


def test_video_details_are_cached_across_calls() -> None:
    pages = [{"items": [_make_playlist_item("id1", "X - Topic", "X - Song")]}]
    video_details = {"id1": {"id": "id1", "contentDetails": {"duration": "PT2M"}}}

    svc = _TestYouTubeService(api_key="")
    svc.youtube = _FakeYouTube(pages=pages, video_details=video_details)

    svc.get_playlist_tracks("PL123", max_results=1, prefer_official=False)
    first_videos_calls = svc.youtube.calls.get("videos.list", 0)

    svc.youtube.reset_playlist_items()
    svc.get_playlist_tracks("PL123", max_results=1, prefer_official=False)
    second_videos_calls = svc.youtube.calls.get("videos.list", 0)

    assert first_videos_calls == 1
    assert second_videos_calls == 1  # unchanged (cache hit)
