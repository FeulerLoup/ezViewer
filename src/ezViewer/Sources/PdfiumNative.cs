using System;
using System.Runtime.InteropServices;

namespace EzViewer.Sources;

/// <summary>PDFium 的最小 P/Invoke 封装。PDFium 不是线程安全的，所有调用都要持有 <see cref="Sync"/>。</summary>
internal static unsafe class PdfiumNative
{
    private const string Lib = "pdfium";

    public static readonly object Sync = new();

    private static bool _initialized;

    public const int FPDF_ANNOT = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF
    {
        public float Width;
        public float Height;
    }

    // Windows 上 C 的 unsigned long 是 32 位
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint m_FileLen;
        public delegate* unmanaged[Cdecl]<IntPtr, uint, byte*, uint, int> m_GetBlock;
        public IntPtr m_Param;
    }

    [DllImport(Lib)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Lib)]
    public static extern IntPtr FPDF_LoadCustomDocument(FPDF_FILEACCESS* access, byte* password);

    [DllImport(Lib)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Lib)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Lib)]
    public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, FS_SIZEF* size);

    [DllImport(Lib)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Lib)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Lib)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Lib)]
    public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport(Lib)]
    public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Lib)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Lib)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Lib)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Lib)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public static string DescribeError(uint code) => code switch
    {
        2 => "文件无法打开",
        3 => "不是有效的 PDF，或文件已损坏",
        4 => "PDF 需要密码，暂不支持加密文档",
        5 => "不支持的 PDF 加密方式",
        6 => "页面不存在或内容有误",
        _ => "未知错误（" + code + "）",
    };
}
