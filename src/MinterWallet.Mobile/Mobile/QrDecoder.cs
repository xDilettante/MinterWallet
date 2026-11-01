using ZXing;
using ZXing.Common;

namespace MinterWallet.Mobile;

// Camera frames stay in memory. Only QR is accepted; no image files or network service are involved.
public static class QrDecoder
{
    public static string? Decode(byte[] luminance, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height != luminance.Length) return null;
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, TryInverted = true },
        };
        return reader.Decode(new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false))?.Text;
    }
}
