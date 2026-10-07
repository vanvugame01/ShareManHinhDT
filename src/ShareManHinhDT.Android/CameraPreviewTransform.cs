using System.Numerics;

namespace ShareManHinhDT.Android;

internal static class CameraPreviewTransform
{
    public static Matrix3x2 Create(int bufferWidth, int bufferHeight, int viewWidth, int viewHeight,
        int sensorOrientation, int displayRotationDegrees)
    {
        if (bufferWidth <= 0 || bufferHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferWidth), "Kích thước ảnh xem trước phải dương.");
        if (sensorOrientation is not (0 or 90 or 180 or 270) || displayRotationDegrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(sensorOrientation), "Góc quay phải là 0, 90, 180 hoặc 270 độ.");

        // TextureView da xoay theo cam bien; chi bu goc man hinh va sua ty le.
        int naturalWidth = sensorOrientation % 180 == 0 ? bufferWidth : bufferHeight;
        int naturalHeight = sensorOrientation % 180 == 0 ? bufferHeight : bufferWidth;
        int rotatedWidth = displayRotationDegrees % 180 == 0 ? naturalWidth : naturalHeight;
        int rotatedHeight = displayRotationDegrees % 180 == 0 ? naturalHeight : naturalWidth;
        float scale = Math.Max((float)viewWidth / rotatedWidth, (float)viewHeight / rotatedHeight);

        return Matrix3x2.CreateScale((float)naturalWidth / viewWidth, (float)naturalHeight / viewHeight)
            * Matrix3x2.CreateTranslation(-naturalWidth / 2f, -naturalHeight / 2f)
            * Matrix3x2.CreateRotation(-displayRotationDegrees * MathF.PI / 180)
            * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation(viewWidth / 2f, viewHeight / 2f);
    }
}
