using System.Reflection;

namespace Agent.Telephone.Media.Resource
{
    internal static class EmbeddedPromptMedia
    {
        private const string ErrorFeedbackDirectory = "error_feedback/";
        private static readonly Assembly s_assembly = typeof(EmbeddedPromptMedia).Assembly;
        private static readonly IReadOnlyDictionary<string, string> s_resourceNames =
            s_assembly.GetManifestResourceNames().ToDictionary(
                static resourceName => NormalizeRelativeFilePath(resourceName),
                StringComparer.OrdinalIgnoreCase);
        private static readonly IReadOnlyCollection<string> s_relativeFilePaths =
            s_resourceNames.Keys.Where(static relativeFilePath => IsCacheableRelativeFilePath(relativeFilePath)).ToArray();

        public static IReadOnlyDictionary<int, string> SipAudioFiles { get; } =
            new Dictionary<int, string>
            {
                [180] = "error_feedback/180.mp3",   // assistant switching ringback
                [402] = "error_feedback/402.mp3",   // payment required
                [403] = "error_feedback/403.mp3",   // forbidden
                [404] = "error_feedback/404.mp3",   // not found
                [480] = "error_feedback/480.mp3",   // assistant temporary unavailable
                [486] = "error_feedback/486.mp3",   // busy here
                [488] = "error_feedback/488.mp3",   // not acceptable here
                [503] = "error_feedback/503.mp3"    // service unavailable
            };

        public static IReadOnlyCollection<string> RelativeFilePaths => s_relativeFilePaths;

        public static Stream OpenRead(string relativeFilePath)
        {
            string normalizedPath = NormalizeRelativeFilePath(relativeFilePath);
            if (!s_resourceNames.TryGetValue(normalizedPath, out string? resourceName))
            {
                throw new FileNotFoundException("找不到嵌入式提示音资源。", normalizedPath);
            }

            return s_assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"无法打开嵌入式提示音资源 {normalizedPath}。");
        }

        private static string NormalizeRelativeFilePath(string relativeFilePath)
        {
            return relativeFilePath.Replace('\\', '/');
        }

        private static bool IsCacheableRelativeFilePath(string relativeFilePath)
        {
            if (!relativeFilePath.StartsWith(ErrorFeedbackDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string fileName = relativeFilePath[ErrorFeedbackDirectory.Length..];
            return fileName.Length == 7
                && fileName[0] is >= '1' and <= '6'
                && char.IsAsciiDigit(fileName[1])
                && char.IsAsciiDigit(fileName[2])
                && fileName[3] == '.'
                && fileName.AsSpan(4).Equals("mp3", StringComparison.OrdinalIgnoreCase);
        }
    }
}
