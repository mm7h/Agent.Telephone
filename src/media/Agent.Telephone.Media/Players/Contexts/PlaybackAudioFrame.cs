using Agent.Telephone.Media.Common.Models;

namespace Agent.Telephone.Media.Players.Contexts;

internal sealed record PlaybackAudioFrame(AudioFrame Frame, int Generation);
