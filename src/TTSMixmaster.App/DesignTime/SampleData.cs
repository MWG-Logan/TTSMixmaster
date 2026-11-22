using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.DesignTime;

/// <summary>Realistic sample collections and tracks for the design-time fakes.</summary>
internal static class SampleData
{
    private static readonly (string Artist, string Title, string Album, int Seconds)[] TrackPool =
    [
        ("Spacehog", "In the Meantime", "Resident Alien", 301),
        ("System of a Down", "Thetawaves", "Hypnotize", 175),
        ("Trivium", "Ignition", "The Sin and the Sentence", 259),
        ("Trivium", "In Waves", "In Waves", 302),
        ("Trivium", "The Phalanx", "Vengeance Falls", 342),
        ("REZZ & Grabbitz", "Someone Else", "Beyond the Senses", 223),
        ("Eli Brown", "Trick Daddy", "Trick Daddy", 384),
        ("Daft Punk", "Technologic", "Human After All", 284),
        ("Lokal", "Flatline", "Flatline", 198),
        ("Russ", "The One", "There's Really a Wolf", 211),
        ("Two Steps From Hell", "Heart of Courage", "Invincible", 117),
        ("Jeremy Soule", "Far Horizons", "The Elder Scrolls V: Skyrim", 307),
        ("Darren Korb", "Build That Wall", "Bastion", 198),
        ("Austin Wintory", "Apotheosis", "Journey", 254),
        ("Lena Raine", "Resurrections", "Celeste", 539),
        ("Yoko Shimomura", "Dearly Beloved", "Kingdom Hearts", 154),
        ("Mick Gordon", "Rip & Tear", "DOOM", 249),
        ("Hans Zimmer", "Time", "Inception", 275),
        ("Ramin Djawadi", "Light of the Seven", "Game of Thrones S6", 589),
        ("Bear McCreary", "Valhalla Awaits", "God of War", 197),
        ("The Witcher 3", "Silver for Monsters", "The Witcher 3: Wild Hunt", 143),
        ("Mili", "world.execute(me);", "Miracle Milk", 216),
        ("Kevin MacLeod", "Monkeys Spinning Monkeys", "Royalty Free", 123),
        ("Kevin MacLeod", "Sneaky Snitch", "Royalty Free", 139),
        ("Adrian von Ziegler", "Celtic Tavern", "Celtic Music", 207),
        ("Peter Gundry", "The Old Tavern", "Medieval Fantasy", 182),
        ("Danny Baranowsky", "Tombtorial", "Crypt of the NecroDancer", 118),
        ("Toby Fox", "Megalovania", "Undertale", 156),
        ("Nobuo Uematsu", "One-Winged Angel", "Final Fantasy VII", 437),
        ("Koji Kondo", "Gerudo Valley", "Ocarina of Time", 112),
        ("C418", "Sweden", "Minecraft - Volume Alpha", 215),
        ("Disasterpeace", "Adventure", "FEZ", 163),
        ("Grant Kirkhope", "Gobi's Valley", "Banjo-Kazooie", 151),
        ("Christopher Larkin", "Hornet", "Hollow Knight", 188),
        ("Ben Prunty", "Mantis", "FTL", 214),
        ("Gareth Coker", "Light of Nibel", "Ori and the Blind Forest", 296),
        ("Kenji Yamamoto", "Brinstar", "Super Metroid", 165),
        ("Harry Gregson-Williams", "Snake Eater", "Metal Gear Solid 3", 245),
        ("Inon Zur", "Fallout 4 Main Theme", "Fallout 4", 156),
        ("Michael Salvatori", "Halo Theme", "Halo: Combat Evolved", 212),
    ];

    public static IReadOnlyList<CollectionSummary> MyCollections(ServiceKind service, string owner) => service switch
    {
        ServiceKind.LastFm =>
        [
            Summary("lastfm:loved", "Loved Tracks", service, CollectionKind.Chart, owner, 18, "Your most loved tracks"),
            Summary("lastfm:top:3month", "Top Tracks · Last 3 months", service, CollectionKind.Chart, owner, 50, "What you've played most lately"),
            Summary("lastfm:top:overall", "Top Tracks · All time", service, CollectionKind.Chart, owner, 50, "Your all-time most played"),
            Summary("lastfm:recent", "Recent Tracks", service, CollectionKind.Chart, owner, 25, "The last things you scrobbled"),
        ],
        ServiceKind.Spotify =>
        [
            Summary("sp:liked", "Liked Songs", service, CollectionKind.Playlist, owner, 212),
            Summary("sp:3fgK2", "Game Night Ambience", service, CollectionKind.Playlist, owner, 24, "Low-key background for long sessions"),
            Summary("sp:9aQx1", "Dungeon Crawl", service, CollectionKind.Playlist, owner, 32, "Torches, traps and tension"),
            Summary("sp:7mPz0", "Tavern Songs", service, CollectionKind.Playlist, owner, 15),
            Summary("sp:top", "Your Top Tracks", service, CollectionKind.Chart, owner, 50, "Spotify's pick of your top tracks"),
        ],
        ServiceKind.YouTube =>
        [
            Summary("PLb0ss", "Boss Battle Themes", service, CollectionKind.Playlist, owner, 20),
            Summary("PLl0fi", "Lo-fi for Strategy Games", service, CollectionKind.Playlist, owner, 40, "Chill beats for turn 40 of 300"),
            Summary("WL", "Watch Later", service, CollectionKind.Playlist, owner, 9),
        ],
        _ => [],
    };

    public static IReadOnlyList<CollectionSummary> Search(ServiceKind service, string query, CollectionKind kind)
    {
        if (kind == CollectionKind.Chart)
            return []; // Charts are per-user; nothing to search.

        var q = query.Trim();
        string[] suffixes = kind == CollectionKind.Album
            ? ["", " (Deluxe Edition)", " — Original Soundtrack"]
            : [" Mix", " Essentials", " for Tabletop", " — Epic Orchestral", " Radio"];
        var owners = new[] { "Tabletop Tunes", "dmsoundboard", "Spotify", "Critical Hits", "nerdcore_nick" };
        return suffixes.Select((suffix, i) => Summary(
                $"{service}:search:{q}:{i}", Title(q) + suffix, service, kind, owners[i % owners.Length], 12 + (i * 7) % 40))
            .ToList();
    }

    public static MusicCollection Collection(CollectionSummary summary)
    {
        var count = Math.Min(summary.TrackCount ?? 20, 60);
        var offset = summary.Id == "lastfm:loved" ? 0 : Math.Abs(StableHash(summary.Id)) % TrackPool.Length;
        var tracks = Enumerable.Range(0, count).Select(i =>
        {
            var (artist, title, album, seconds) = TrackPool[(offset + i) % TrackPool.Length];
            return new Track
            {
                Artist = artist,
                Title = title,
                Album = album,
                Duration = TimeSpan.FromSeconds(seconds),
                Service = summary.Service,
                ServiceId = $"{summary.Id}:{i}",
                TrackNumber = i + 1,
                PlayCount = summary.Service == ServiceKind.LastFm ? 120 - i * 3 : 0,
            };
        }).ToList();
        return new MusicCollection { Summary = summary with { TrackCount = tracks.Count }, Tracks = tracks };
    }

    /// <summary>A summary for a pasted link the fakes have never seen.</summary>
    public static CollectionSummary FromLink(ServiceKind service, string url) =>
        Summary(url, service == ServiceKind.LastFm ? "Shared chart" : "Shared playlist", service,
            service == ServiceKind.LastFm ? CollectionKind.Chart : CollectionKind.Playlist, "someone else", 16);

    private static CollectionSummary Summary(string id, string name, ServiceKind service, CollectionKind kind,
        string owner, int count, string description = "") => new()
    {
        Id = id, Name = name, Service = service, Kind = kind, Owner = owner, TrackCount = count, Description = description,
    };

    private static string Title(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s)
                h = h * 31 + c;
            return h;
        }
    }
}
