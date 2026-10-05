using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Agent.Telephone.Media.Abstractions;
using Agent.Telephone.Media.Abstractions.Common.Dtos;
using Agent.Telephone.Media.Editors;
using Agent.Telephone.Media.Encoders;
using Agent.Telephone.Media.Encoders.FFmpeg;
using Agent.Telephone.Media.Mixers;
using Agent.Telephone.Media.Players;
using Agent.Telephone.Media.Players.WorkPool;
using Agent.Telephone.Media.Subtitle;
using Agent.Telephone.Media.Utilities;

namespace Agent.Telephone.Media
{
    /// <summary>
    /// 提供用于创建媒体服务实例的工厂方法。
    /// </summary>
    /// <remarks>此工厂类包含为不同输入源创建音频播放器的方法，例如 URL 和流。
    /// 创建的音频播放器已预先配置默认日志行为。
    /// FFmpeg 集成方案参考了 https://github.com/luthfiampas/Bufdio。
    /// </remarks>
    public static class MediaFactory
    {
        private static readonly IAudioDecodeScheduler s_audioDecodeScheduler = new AudioDecodeScheduler(new AudioPlayerOptions());

        /// <summary>
        /// 从指定路径注册 FFmpeg 二进制文件。
        /// </summary>
        /// <param name="ffmpegPath">FFmpeg 二进制文件所在路径。</param>
        public static void InitializeFFmpeg(string ffmpegPath = "./ffmpeg/")
        {
            FFmpegStartup.RegisterFFmpegBinaries(ffmpegPath);
        }

        /// <summary>
        /// 检查系统是否已安装 FFmpeg，并获取已安装的版本。
        /// </summary>
        /// <param name="ffmpegVersion">此方法返回时包含系统已安装的 FFmpeg 版本；未安装时为空字符串。此参数未经初始化即传入。</param>
        /// <returns>如果已安装 FFmpeg，则为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
        public static bool CheckFFmpegInstalled(out string ffmpegVersion)
        {
            return FFmpegStartup.CheckFFmpegInstalled(out ffmpegVersion);
        }
    }
}
