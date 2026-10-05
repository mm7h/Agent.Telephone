using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace Agent.Telephone.Helper;

internal static class FFmpegHelper
{
    private const int ErrorBufferSize = 1024;

    public static bool FFIsEOF(this int code)
    {
        return code == ffmpeg.AVERROR_EOF;
    }

    public static bool FFIsError(this int code)
    {
        return code < 0;
    }

    public static int FFGuard(this int code)
    {
        if (!code.FFIsError())
        {
            return code;
        }

        throw new InvalidOperationException($"FFmpeg error: {code.FFErrorToText()}");
    }

    public static string FFErrorToText(this int code)
    {
        unsafe
        {
            var buffer = stackalloc byte[ErrorBufferSize];
            ffmpeg.av_strerror(code, buffer, ErrorBufferSize);

            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "Unknown Error";
        }
    }
}
