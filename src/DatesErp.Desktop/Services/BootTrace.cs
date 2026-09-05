using System;
using System.IO;

namespace DatesErp.Desktop.Services;

/// <summary>
/// أثر إقلاع يُكتب في ملف بجانب ملف التشغيل مباشرة (وليس في LocalAppData) ليسهل العثور عليه.
/// يبدأ من أول لحظة في التطبيق ليسجل أين يتوقف الإقلاع بالضبط حتى لو كان العطل مبكراً جداً.
/// </summary>
public static class BootTrace
{
    private static readonly object _lock = new();
    private static string _path;

    private static string Path
    {
        get
        {
            if (_path == null)
            {
                try { _path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot_trace.txt"); }
                catch { _path = "boot_trace.txt"; }
            }
            return _path;
        }
    }

    public static void Step(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(Path, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { /* لا نفشل أبداً بسبب فشل التسجيل نفسه */ }
    }

    public static void Fail(string stage, Exception ex)
    {
        try
        {
            lock (_lock)
            {
                var txt = $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss.fff}] فشل في مرحلة [{stage}]: " +
                          (ex?.GetType().Name ?? "?") + ": " + (ex?.Message ?? "?") + Environment.NewLine +
                          (ex?.InnerException != null ? "   السبب الداخلي: " + ex.InnerException.Message + Environment.NewLine : "") +
                          (ex?.StackTrace ?? "") + Environment.NewLine +
                          new string('-', 70) + Environment.NewLine;
                File.AppendAllText(Path, txt);
            }
        }
        catch { }
    }

    /// <summary>مسار الملف — لعرضه للمستخدم.</summary>
    public static string FilePath => Path;
}
