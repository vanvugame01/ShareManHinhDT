using Android.Runtime;

namespace ShareManHinhDT.Android;

internal static class AndroidServices
{
    public static T Require<T>(Context context, string name) where T : class, IJavaObject
    {
        var service = context.GetSystemService(name)
            ?? throw new InvalidOperationException($"Không tìm thấy dịch vụ Android: {name}.");
        string managedType = service.GetType().FullName ?? service.GetType().Name;
        using var javaClass = service.Class;
        string javaType = javaClass?.Name ?? "không rõ";
        global::Android.Util.Log.Info("ShareManHinhDT.Services",
            $"Dịch vụ {name}; wrapper {managedType}; Java {javaType}; kiểu yêu cầu {typeof(T).FullName}");
        try
        {
            // Chuyen kieu qua JNI de tao proxy cho interface Java.
            return Java.Interop.JavaObjectExtensions.JavaCast<T>(service)
                ?? throw new InvalidOperationException("JNI trả về dịch vụ rỗng.");
        }
        catch (Exception ex)
        {
            string detail = $"Không chuyển được dịch vụ {name} ({managedType}, Java {javaType}) sang {typeof(T).Name}.";
            global::Android.Util.Log.Error("ShareManHinhDT.Services", $"{detail}\n{ex}");
            throw new InvalidOperationException(detail, ex);
        }
    }
}
