namespace Agent.Telephone.Codex.Abstractions.Common.Configs
{
    /// <summary>
    /// Codex 电话助手的运行配置。
    /// </summary>
    public sealed class CodexAssistantOptions
    {
        /// <summary>
        /// Codex CLI 可执行文件路径或 PATH 中的命令名。Windows 默认优先使用 Codex Desktop 安装的 CLI；其他系统使用 PATH 中的命令名。
        /// </summary>
        public string ExecutablePath { get; set; } = GetDefaultExecutablePath();

        /// <summary>
        /// 保存电话身份与 Codex Thread 对应关系的 SQLite 文件路径。
        /// </summary>
        public string ThreadDatabasePath { get; set; } = Path.Combine(Environment.CurrentDirectory, "data", "codex", "threads.db");

        /// <summary>
        /// 交给 Codex 的模型名。
        /// </summary>
        public string ModelName { get; set; } = string.Empty;

        /// <summary>
        /// Codex 的推理强度。
        /// </summary>
        public string ReasoningEffort { get; set; } = string.Empty;

        /// <summary>
        /// 所有电话任务统一使用的 Codex Project 工作目录。
        /// </summary>
        public string WorkingDirectory { get; set; } = Path.Combine(Environment.CurrentDirectory, "data", "codex");

        /// <summary>
        /// 验证并规范化文件系统路径。
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(this.ExecutablePath))
            {
                throw new ArgumentException("Codex executable path cannot be empty.", nameof(this.ExecutablePath));
            }
            if (string.IsNullOrWhiteSpace(this.ThreadDatabasePath))
            {
                throw new ArgumentException("Codex thread database path cannot be empty.", nameof(this.ThreadDatabasePath));
            }
            if (string.IsNullOrWhiteSpace(this.ModelName))
            {
                throw new ArgumentException("Codex model name cannot be empty.", nameof(this.ModelName));
            }
            if (string.IsNullOrWhiteSpace(this.ReasoningEffort))
            {
                throw new ArgumentException("Codex reasoning effort cannot be empty.", nameof(this.ReasoningEffort));
            }
            if (string.IsNullOrWhiteSpace(this.WorkingDirectory))
            {
                throw new ArgumentException("Codex working directory cannot be empty.", nameof(this.WorkingDirectory));
            }

            this.ThreadDatabasePath = Path.GetFullPath(this.ThreadDatabasePath);
            this.WorkingDirectory = Path.GetFullPath(this.WorkingDirectory);
        }

        private static string GetDefaultExecutablePath()
        {
            if (!OperatingSystem.IsWindows())
            {
                return "codex";
            }

            string desktopBinDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI",
                "Codex",
                "bin");
            if (!Directory.Exists(desktopBinDirectory))
            {
                return "codex";
            }

            try
            {
                string? executablePath = Directory.EnumerateFiles(desktopBinDirectory, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                return executablePath ?? "codex";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "codex";
            }
        }
    }
}
