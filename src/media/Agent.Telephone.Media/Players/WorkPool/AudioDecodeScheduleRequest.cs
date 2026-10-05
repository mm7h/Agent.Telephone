using Agent.Telephone.Media.Players.Contexts;

namespace Agent.Telephone.Media.Players.WorkPool;

/// <summary>
/// 表示已进入调度队列的一次播放上下文解码请求。
/// </summary>
internal readonly record struct AudioDecodeScheduleRequest(
    AudioPlaybackContext Context,
    int ScheduleVersion,
    int Generation,
    AudioDecodeWorkPriority Priority,
    long DeadlineTimestamp);
