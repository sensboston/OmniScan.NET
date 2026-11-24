using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OmniScan.Net.Imaging;
using OmniScan.Net.Ocr;

namespace OmniScan.Net.App;

/// <summary>
/// The text of the PDF pages, read in the background as soon as a page is added, so that it is
/// usually ready when the PDF is saved. A turned page is a new page and is read again.
/// </summary>
public sealed class PageTexts
{
    private readonly ConditionalWeakTable<PdfPage, Task<IReadOnlyList<TextWord>?>> _reads = [];

    /// <summary>Starts reading the page, unless it is read already.</summary>
    public void Start(PdfPage page, string language) => Get(page, language);

    /// <summary>The words of the page; null when it could not be read.</summary>
    public Task<IReadOnlyList<TextWord>?> Get(PdfPage page, string language) =>
        _reads.GetValue(page, p => Task.Run(() => ReadAsync(p, language)));

    private static async Task<IReadOnlyList<TextWord>?> ReadAsync(PdfPage page, string language)
    {
        try
        {
            return await PageReader.ReadAsync(PdfPages.Decode(page), language).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Without its text the page is still saved, as an image only.
            return null;
        }
    }
}
