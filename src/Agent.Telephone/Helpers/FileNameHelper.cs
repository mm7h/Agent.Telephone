namespace Agent.Telephone.Helpers
{
    internal static class FileNameHelper
    {
        public static string CreateAudioFileName(
            string providerType,
            string? fromNumber,
            string? toNumber,
            string index,
            string extension)
        {
            return $"{ToFileNameComponent(providerType)}_{ToFileNameComponent(fromNumber)}_{ToFileNameComponent(toNumber)}_{ToFileNameComponent(index)}.{extension.TrimStart('.')}";
        }

        public static string GetIndex(string value, string deviceId)
        {
            string prefix = $"{deviceId}_";
            return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
        }

        private static string ToFileNameComponent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string component = new string(value.Where(character => !invalidChars.Contains(character)).ToArray());
            return string.IsNullOrWhiteSpace(component) ? "unknown" : component;
        }
    }
}
