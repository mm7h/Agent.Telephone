namespace Agent.Telephone.Common.BuildConfigs
{
    internal record AudioFileCachingBuildConfig(string PromptMediaPath, IDictionary<int, string> PromptMediaConfigs);
}
