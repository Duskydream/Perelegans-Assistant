using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace Perelegans.Services;

public sealed class ScreenContextService
{
    public string CapturePrimaryScreenAsJpegBase64(int maxWidth = 1280, long jpegQuality = 70L)
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? Forms.Screen.AllScreens[0].Bounds;
        using var screenshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(screenshot))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        using var resized = ResizeToMaxWidth(screenshot, maxWidth);
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(jpegQuality, 1L, 100L));
        resized.Save(stream, codec, encoderParameters);

        return Convert.ToBase64String(stream.ToArray());
    }

    private static Bitmap ResizeToMaxWidth(Bitmap source, int maxWidth)
    {
        if (source.Width <= maxWidth)
        {
            return new Bitmap(source);
        }

        var ratio = maxWidth / (double)source.Width;
        var width = maxWidth;
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
        graphics.DrawImage(source, 0, 0, width, height);

        return resized;
    }
}
