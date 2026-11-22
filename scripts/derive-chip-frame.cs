// Regenerates src/TTSMixmaster.Core/Assets/Chip/frame.png from front.png.
//
//   dotnet run scripts/derive-chip-frame.cs [-- --preview <frame-over-magenta.png>]
//
// The cut-out uses ChipGeometry / ChipFrame from TTSMixmaster.Core, so the renderer and this tool share
// the same window constants. The ChipFrameTests "committed frame matches derivation" test fails if
// frame.png drifts from what this produces.
#:project ../src/TTSMixmaster.Core/TTSMixmaster.Core.csproj
#:property UseAppHost=false
#:property PublishAot=false

using SkiaSharp;
using TTSMixmaster.Core.Imaging;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var chip = Path.Combine(root, "src", "TTSMixmaster.Core", "Assets", "Chip");
var frontPath = Path.Combine(chip, "front.png");
var framePath = Path.Combine(chip, "frame.png");

using var front = ChipFrame.Decode(File.ReadAllBytes(frontPath));
using var frame = ChipFrame.Derive(front);
File.WriteAllBytes(framePath, ChipFrame.Encode(frame));
Console.WriteLine($"wrote {framePath}");

var preview = args.SkipWhile(a => a != "--preview").Skip(1).FirstOrDefault();
if (preview is not null)
{
    using var surface = SKSurface.Create(new SKImageInfo(ChipGeometry.Size, ChipGeometry.Size, SKColorType.Rgba8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Magenta);
    using var image = SKImage.FromBitmap(frame);
    surface.Canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default, null);
    using var snapshot = surface.Snapshot();
    using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(preview, png.ToArray());
    Console.WriteLine($"wrote {preview}");
}
