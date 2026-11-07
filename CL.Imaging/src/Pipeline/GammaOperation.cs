using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class GammaOperation(double gamma) : ColorOperation
{
    public override SKColorFilter CreateFilter()
    {
        var table = new byte[256];
        var identity = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            identity[i] = (byte)i;
            table[i] = (byte)Math.Clamp(Math.Round(255 * Math.Pow(i / 255.0, 1 / gamma)), 0, 255);
        }
        return SKColorFilter.CreateTable(identity, table, table, table);
    }
}
