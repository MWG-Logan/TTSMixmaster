# Chip images

`ChipImageBuilder` renders the front face of a music chip (the TTS Custom Tile): the collection's
art behind the Morningwood Gaming chip frame, with the collection name on top.

```csharp
var builder = new ChipImageBuilder(httpClient);
ChipImage front = await builder.BuildFrontAsync(collection, new ChipOptions(), ct);
ChipImage back  = ChipImageBuilder.BackImage;   // the original chip back, unchanged
```

`ChipImage` holds `Png` (bytes), `Hash` (lowercase SHA-256 of those bytes), `Width`/`Height`,
`ArtSource` (`Collection`, `Track` or `Generated`) and `FileName` (`chip-<hash>.png`).
`ChipImageBuilder.RenderFront(bytes, title, service, options)` renders from art you already have.

## Pipeline

1. **Art.** `CollectionSummary.ThumbnailUrl`, else the first track that has a `ThumbnailUrl`, downloaded
   with the injected `HttpClient` (http/https only, at most 20 MB). A failed download, an error status,
   a timeout or bytes that don't decode all move on to the next candidate, then to a generated
   background. Only cancellation of the caller's token throws.
2. **Window.** The art is cover-fitted (centre-cropped) into the window. Symmetric black letterbox or
   pillarbox bars (YouTube's 4:3 thumbnails of 16:9 videos) are trimmed first. Small art is upscaled with a
   Mitchell cubic; large art is downscaled with mipmaps. With no art, a dark service-tinted gradient with
   a faint record is drawn (Spotify green, YouTube red, Last.fm crimson, slate otherwise); never the crest.
3. **Frame.** `Assets/Chip/frame.png` is drawn on top, pixel for pixel at 1x.
4. **Title.** Cinzel Bold, white, centred in the lower third (single-line baseline y=386), with a bottom
   scrim and a soft drop shadow so it reads over any art. Cinzel's lowercase letters are small caps, so
   "Woody's Fine EDM" looks like the original chip's title. Fitting: shrink 34→22 px on one line, then two
   balanced lines at 28→18 px (breaking at spaces, or between CJK characters with kinsoku), then ellipsize
   the second line, preferably on a word. Text is shaped with HarfBuzz (kerning, emoji ZWJ sequences,
   complex scripts). Characters Cinzel lacks (CJK, Cyrillic, emoji, …) fall back per character to a
   system font via `SKFontManager.MatchCharacter`; if no installed font has a glyph it renders as a box.
5. **Output.** A PNG and its SHA-256. Rendering is deterministic for the same art, title, options and
   installed fonts (fonts only matter for fallback characters), so the hash is a stable content address.

### Size: 1x by default

The default output is the native 478x478. `ChipOptions.Scale = 2` renders 956x956: the title and art
are sharper when zoomed in TTS, but the frame only exists at 478 and is resampled, and the PNG is about
3x larger (≈1 MB vs ≈320 KB for photo art). A chip is small on the table and every chip in a save is a
separate texture download, so 1x keeps the frame pixel-exact and loads fastest; use 2x if alt-zoom
legibility of long titles matters more.

## Integration (not wired yet)

The chip should be uploaded beside the audio and its URL handed to the formatter:

1. Build the front once per collection (`BuildFrontAsync`) after loading its tracks.
2. Upload `front.Png` to the same container as the audio as a content-addressed blob, e.g.
   `images/{front.FileName}` (`images/chip-<sha256>.png`), with `ContentType = ChipImage.ContentType`
   (`image/png`) and `ContentDisposition = inline`. Because the name is the content hash, an existing
   blob with that name is already correct: skip the upload (no freshness check needed), and identical
   chips across collections share one blob.
3. Use the blob's public URL without any SAS query (`blob.Uri.GetLeftPart(UriPartial.Path)`, as the
   audio upload does) as `TtsOutputOptions.PrimaryImageUrl`.
4. `SecondaryImageUrl` can stay empty: `TtsFormatter` then uses the Steam-hosted original back, which
   is identical to `ChipImageBuilder.BackImage`. To self-host it, upload `BackImage.Png` the same way.

If the upload fails, leave `PrimaryImageUrl` empty and `TtsFormatter` falls back to the original
Steam-hosted MWG front.

## Window geometry

`ChipGeometry` holds the window as constants, in pixel coordinates of the 478x478 art (pixel (x, y)
covers [x, x+1] x [y, y+1]):

| | value |
|---|---|
| Rect | left = top = 31⅔, right = bottom = 446⅓ (414⅔ px square) |
| Notch | the bottom-left corner is cut by the 45° line x − y = −354.5 |
| Polygon | (31⅔, 31⅔) → (446⅓, 31⅔) → (446⅓, 446⅓) → (91⅚, 446⅓) → (31⅔, 386⅙) |

Measured from `front.png`: the panel's straight edges are pixels 31 and 446, which are exactly ⅔ frame
slate (#262632 → #191921), and corner pixel (31, 31) is (⅔)². The notch diagonal is a 45° staircase:
in row y, pixel y−356 is frame, y−355 is a dark edge pixel, y−354 onward is black; the line sits on the
staircase's midline.

## Assets

`Assets/Chip/` (embedded resources):

- `front.png`: the original MWG chip front (478x478), from
  `https://steamusercontent-a.akamaihd.net/ugc/9672878331288570/AE7A2999E8CD0EFF71210D7961A41E1F87F9DE78/`.
- `back.png`: the original chip back (477x478), from
  `https://steamusercontent-a.akamaihd.net/ugc/1778335968028979741/9DA6ABA2450EBDA4E967816C4FA92289A638DB53/`.
- `frame.png`: derived from `front.png`. Regenerate with
  `dotnet run scripts/derive-chip-frame.cs [-- --preview frame-over-magenta.png]`.
  Pixels fully inside the window are transparent, pixels outside are copied byte for byte, and edge
  pixels get alpha = 1 − exact window coverage with the colour of their neighbouring frame pixels (not
  their own, which is blended with the black panel and would leave a dark fringe). The test
  `Committed_frame_matches_the_derivation_from_front` fails if the file drifts from the script.
- `Fonts/Cinzel-Bold.ttf` and `Fonts/OFL.txt`: see below.

## Licensing

- **Cinzel** (Natanael Gama / The Cinzel Project Authors, https://github.com/NDISCOVER/Cinzel) is under
  the SIL Open Font License 1.1. The font is embedded unmodified; `Fonts/OFL.txt` ships with the app as
  `Licenses/Cinzel-OFL.txt`. OFL allows bundling and embedding in software, including commercial use; the
  font may not be sold on its own, and a modified version may not use the reserved name.
- **SkiaSharp** and **SkiaSharp.HarfBuzz** (with HarfBuzzSharp) are MIT. No System.Drawing, no ImageSharp.

## Platforms

`SkiaSharp.NativeAssets.Win32` and `HarfBuzzSharp.NativeAssets.Win32` come in transitively and carry
win-x64, win-arm64 and win-x86 native libraries; RID-specific publishes of the app pick the right one.
Running the tests on Linux would need `SkiaSharp.NativeAssets.Linux` and `HarfBuzzSharp.NativeAssets.Linux`
added to the test project.
