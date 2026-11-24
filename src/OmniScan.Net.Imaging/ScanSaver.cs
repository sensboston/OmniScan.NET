using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Leptonica;
using OmniScan.Net.Core;

namespace OmniScan.Net.Imaging;

/// <summary>What to do with an acquired frame, captured from the settings when the scan starts.</summary>
/// <param name="Frames">Frames adjusted by the user on the preview, in page-width units; null: detect automatically.</param>
/// <param name="SaveBlank">Save even when nothing is on the glass (the "save anyway" after a blank scan).</param>
/// <param name="GuessContent">Also tell whether the scan is a document or photos (<see cref="SaveResult.Content"/>).</param>
public sealed record SaveOptions(
    string RootFolder,
    DateTime Date,
    OutputFormat Format,
    int JpegQuality,
    int Rotation,
    PipelineOptions Processing,
    IReadOnlyList<CutFrame>? Frames = null,
    bool SaveBlank = false,
    bool GuessContent = false);

/// <summary>A preview: the whole bed as an image and the frames that would be cut, in page-width units.</summary>
public sealed record PreviewResult(byte[] PageBmp, IReadOnlyList<CutFrame> Frames, ContentKind Content = ContentKind.Unknown);

/// <summary>
/// The files written for one scan, the images they were encoded from (kept for lossless
/// re-saving after a manual rotation) and a small preview of the result.
/// </summary>
public sealed record SaveResult(PageKind Kind, IReadOnlyList<string> Files, IReadOnlyList<Pix> Images, byte[] PreviewBmp,
    ContentKind Content = ContentKind.Unknown);

/// <summary>Turns an acquired frame into saved files: rotate, process, encode, write to the day folder.</summary>
public static class ScanSaver
{
    public const int PreviewMaxSide = 1600;

    /// <summary>Decodes and rotates a frame; the starting point of every processing path.</summary>
    public static Pix Prepare(ScannedFrame frame, int rotation) =>
        FrameCodec.Rotate(FrameCodec.Decode(frame), rotation);

    public static SaveResult Save(ScannedFrame frame, SaveOptions options)
    {
        Pix page = Prepare(frame, options.Rotation);

        // Nothing on the glass: no file (the caller offers "save anyway").
        if (!options.SaveBlank && options.Frames is null && options.Processing != PipelineOptions.None
            && BedAnalyzer.Analyze(page).Objects.Count == 0)
            return new SaveResult(PageKind.Empty, [], [], FrameCodec.PreviewBmp(page, PreviewMaxSide));

        IReadOnlyList<CutFrame> frames = options.Frames is { } manual
            ? manual.Select(f => f.Denormalize(page.Width)).ToList()
            : ScanPipeline.Plan(page, options.Processing);
        PipelineResult processed = ScanPipeline.Cut(page, frames, options.Processing);

        string folder = ScanOutput.DayFolder(options.RootFolder, options.Date);
        string extension = OutputFormats.Extension(options.Format);
        List<string> files = [];
        foreach (Pix image in processed.Images)
        {
            byte[] data = FrameCodec.Encode(image, options.Format, options.JpegQuality);
            using FileStream file = ScanOutput.CreateNext(folder, extension);
            file.Write(data);
            files.Add(file.Name);
        }

        // One result: show it. Several photos: show the bed with what was cut out.
        byte[] preview = processed.Images.Count == 1
            ? FrameCodec.PreviewBmp(processed.Images[0], PreviewMaxSide)
            : PreviewWithOutlines(page, processed.Outlines);
        ContentKind content = options.GuessContent ? ContentGuess.Classify(page) : ContentKind.Unknown;
        return new SaveResult(processed.Kind, files, processed.Images, preview, content);
    }

    /// <summary>
    /// Turns a saved image by a multiple of 90 degrees and writes the file again from the in-memory
    /// image (no second JPEG generation), with <paramref name="comment"/> inside a JPEG. Returns the rotated image.
    /// </summary>
    public static Pix Resave(string path, Pix image, int degrees, OutputFormat format, int jpegQuality, string? comment = null)
    {
        Pix rotated = FrameCodec.Rotate(image, ((degrees % 360) + 360) % 360);
        byte[] data = FrameCodec.Encode(rotated, format, jpegQuality);
        if (format == OutputFormat.Jpeg && !string.IsNullOrEmpty(comment))
            data = JpegComment.Set(data, comment);
        Replace(path, data);
        return rotated;
    }

    /// <summary>Puts a comment into a saved JPEG file (the image data is kept as it is).</summary>
    public static void Comment(string path, string? comment) =>
        Replace(path, JpegComment.Set(File.ReadAllBytes(path), comment));

    /// <summary>Writes a file through a temporary one, so a failure never leaves half a file.</summary>
    private static void Replace(string path, byte[] data)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A small BMP of an image for a thumbnail.</summary>
    public static byte[] Thumbnail(Pix image, int maxSide) => FrameCodec.PreviewBmp(image, maxSide);

    /// <summary>A preview scan is shown, never saved: the bed and the frames that would be cut.</summary>
    public static PreviewResult Preview(ScannedFrame frame, int rotation, PipelineOptions processing, bool guessContent = false)
    {
        Pix page = Prepare(frame, rotation);
        IReadOnlyList<CutFrame> frames = ScanPipeline.Plan(page, processing);
        ContentKind content = guessContent ? ContentGuess.Classify(page) : ContentKind.Unknown;
        return new PreviewResult(PreviewWithOutlines(page, []), frames.Select(f => f.Normalize(page.Width)).ToList(), content);
    }

    internal static byte[] PreviewWithOutlines(Pix page, IReadOnlyList<RotatedRect> outlines)
    {
        int side = Math.Max(page.Width, page.Height);
        double k = side > PreviewMaxSide ? (double)PreviewMaxSide / side : 1.0;
        Pix preview = k < 1 ? page.ScaleToSize(Math.Max(1, (int)(page.Width * k)), Math.Max(1, (int)(page.Height * k))) : page;
        if (outlines.Count == 0)
            return Pix.WriteMemBmp(preview);

        preview = preview.Depth == 32 ? Pix.Copy(null, preview) : preview.ConvertTo32();
        int width = Math.Max(2, (int)Math.Round(Math.Max(preview.Width, preview.Height) / 400.0));
        foreach (RotatedRect r in outlines)
        {
            Pta pta = Pta.Create(4);
            foreach ((double x, double y) in r.Scale(k).Corners())
                pta.AddPt((float)x, (float)y);
            preview.RenderPolylineArb(pta, width, 0, 200, 120, 1);
        }

        return Pix.WriteMemBmp(preview);
    }
}
