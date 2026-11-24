using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OmniScan.Net.Core;

namespace OmniScan.Net.Tests;

public sealed class ScanOutputTests
{
    [Fact]
    public void DayFolder_UsesIsoDate()
    {
        string folder = ScanOutput.DayFolder(@"C:\Scans", new DateTime(2026, 3, 7, 23, 59, 0));
        Assert.Equal(@"C:\Scans\2026-03-07", folder);
    }

    [Fact]
    public void CreateNext_CreatesMissingFolder_StartsAtOne()
    {
        using TempFolder temp = new();
        string day = Path.Combine(temp.Path, "2026-11-23");

        using FileStream file = ScanOutput.CreateNext(day, ".png");

        Assert.Equal(Path.Combine(day, "scan_001.png"), file.Name);
        Assert.True(File.Exists(file.Name));
    }

    [Fact]
    public void CreateNext_ContinuesAfterHighest_DoesNotFillGaps()
    {
        using TempFolder temp = new();
        temp.Touch("scan_001.png");
        temp.Touch("scan_005.png");

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".png");

        Assert.Equal("scan_006.png", Path.GetFileName(file.Name));
    }

    [Fact]
    public void CreateNext_NumbersRunThroughAllFormats()
    {
        using TempFolder temp = new();
        temp.Touch("scan_001.png");
        temp.Touch("scan_002.jpg");
        temp.Touch("scan_003.PDF");

        using FileStream pdf = ScanOutput.CreateNext(temp.Path, "pdf");
        using FileStream jpg = ScanOutput.CreateNext(temp.Path, ".jpg");

        Assert.Equal("scan_004.pdf", Path.GetFileName(pdf.Name));
        Assert.Equal("scan_005.jpg", Path.GetFileName(jpg.Name));
    }

    [Fact]
    public void CreateNext_OldFolderWithSharedNumbers_ContinuesAfterHighest()
    {
        using TempFolder temp = new();
        temp.Touch("scan_001.jpg");
        temp.Touch("scan_001.pdf");
        temp.Touch("scan_002.jpg");

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".pdf");

        Assert.Equal("scan_003.pdf", Path.GetFileName(file.Name));
    }

    [Theory]
    [InlineData("scan_007.tif")]
    [InlineData("scan_007.jpg.bak")]
    public void CreateNext_OtherFilesDoNotCount(string foreign)
    {
        using TempFolder temp = new();
        temp.Touch("scan_002.pdf");
        temp.Touch(foreign);

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".jpg");

        Assert.Equal("scan_003.jpg", Path.GetFileName(file.Name));
    }

    [Fact]
    public void CreateNext_MixedConcurrentWriters_GetDistinctNumbers()
    {
        using TempFolder temp = new();
        const int writers = 24;
        FileStream[] created = new FileStream[writers];
        try
        {
            Parallel.For(0, writers, i => created[i] = ScanOutput.CreateNext(temp.Path, i % 2 == 0 ? ".jpg" : ".pdf"));
            string[] numbers = created.Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToArray();
            Assert.Equal(writers, numbers.Distinct().Count());
        }
        finally
        {
            foreach (FileStream f in created)
                f?.Dispose();
        }
    }

    [Theory]
    [InlineData("photo_009.png")]
    [InlineData("scan_009.png.bak")]
    [InlineData("scan_009.pngx")]
    [InlineData("scan_abc.png")]
    [InlineData("scan_.png")]
    [InlineData("scan_9a.png")]
    [InlineData("myscan_009.png")]
    public void CreateNext_IgnoresForeignNames(string foreign)
    {
        using TempFolder temp = new();
        temp.Touch("scan_002.png");
        temp.Touch(foreign);

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".png");

        Assert.Equal("scan_003.png", Path.GetFileName(file.Name));
    }

    [Fact]
    public void CreateNext_IsCaseInsensitive()
    {
        using TempFolder temp = new();
        temp.Touch("SCAN_004.PNG");

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".png");

        Assert.Equal("scan_005.png", Path.GetFileName(file.Name));
    }

    [Fact]
    public void CreateNext_GrowsPastThreeDigits()
    {
        using TempFolder temp = new();
        temp.Touch("scan_999.jpg");

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".jpg");

        Assert.Equal("scan_1000.jpg", Path.GetFileName(file.Name));
    }

    [Fact]
    public void CreateNext_NeverOverwrites()
    {
        using TempFolder temp = new();
        string existing = Path.Combine(temp.Path, "scan_001.jpg");
        File.WriteAllText(existing, "keep");

        using (ScanOutput.CreateNext(temp.Path, ".jpg"))
        {
        }

        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public void CreateNext_ConcurrentWriters_GetDistinctNames()
    {
        using TempFolder temp = new();
        const int writers = 32;
        List<FileStream> files = [];
        try
        {
            FileStream[] created = new FileStream[writers];
            Parallel.For(0, writers, i => created[i] = ScanOutput.CreateNext(temp.Path, ".png"));
            files.AddRange(created);

            string[] names = files.Select(f => Path.GetFileName(f.Name)).Order().ToArray();
            string[] expected = Enumerable.Range(1, writers).Select(n => $"scan_{n:D3}.png").ToArray();
            Assert.Equal(expected, names);
        }
        finally
        {
            foreach (FileStream f in files)
                f.Dispose();
        }
    }

    [Fact]
    public void CreateNext_CustomPrefix()
    {
        using TempFolder temp = new();
        temp.Touch("scan_007.png");

        using FileStream file = ScanOutput.CreateNext(temp.Path, ".png", "photo");

        Assert.Equal("photo_001.png", Path.GetFileName(file.Name));
    }
}
