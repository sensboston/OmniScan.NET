using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OmniScan.Net.Imaging;
using OmniScan.Net.Ocr;

namespace OmniScan.Net.App;

/// <summary>What was read on a PDF page: the turn that makes it upright (usually 0) and the words in that position.</summary>
public sealed record PageText(int Turn, IReadOnlyList<TextWord> Words);

/// <summary>
/// The text of the PDF pages, read in the background as soon as a page is added, so that it is
/// usually ready when the PDF is saved. Reading also tells when a page is still upside down or
/// sideways. A turned page is a new page; its words are handed over when they are known already.
/// </summary>
public sealed class PageTexts
{
    private readonly ConditionalWeakTable<PdfPage, Task<PageText?>> _reads = [];

    /// <summary>The reading of the page (which way up, and the words), now or before; null when it could not be read.</summary>
    public Task<PageText?> Get(PdfPage page, string language) =>
        _reads.GetValue(page, p => Task.Run(() => ReadAsync(p, language)));

    /// <summary>Gives a page turned by the program the words read on the page before the turn.</summary>
    public void Known(PdfPage page, IReadOnlyList<TextWord> words) =>
        _reads.AddOrUpdate(page, Task.FromResult<PageText?>(new PageText(0, words)));

    private static async Task<PageText?> ReadAsync(PdfPage page, string language)
    {
        try
        {
            (int turn, IReadOnlyList<TextWord> words) = await PageReader.ReadUprightAsync(PdfPages.Decode(page), language).ConfigureAwait(false);
            return new PageText(turn, words);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Without its text the page is still saved, as an image only.
            return null;
        }
    }
}
