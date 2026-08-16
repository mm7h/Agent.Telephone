using System.Reflection;

namespace Agent.Telephone.Media.Resource
{
    internal static class EmbeddedPromptMedia
    {
        private static readonly Assembly s_assembly = typeof(EmbeddedPromptMedia).Assembly;
        private static readonly IReadOnlyDictionary<string, string> s_resourceNames =
            s_assembly.GetManifestResourceNames().ToDictionary(
                static resourceName => NormalizeRelativeFilePath(resourceName),
                StringComparer.OrdinalIgnoreCase);
        private static readonly IReadOnlyCollection<string> s_relativeFilePaths =
            s_resourceNames.Keys.ToArray();

        public static IReadOnlyDictionary<int, string> SipAudioFiles { get; } =
            new Dictionary<int, string>
            {
                [180] = "error_feedback/180.mp3",   // assistant switching ringback
                [402] = "error_feedback/402.mp3",   // payment required
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
    }
}
