using Agent.Telephone.Resources.OnnxModels.VAD.Models;

namespace Agent.Telephone.Resources.OnnxModels
{
    internal interface IVadOnnxModel : IOnnxModel
    {
        float Infer(float[] audioSamples, int sampleRate, SileroModelState modelState);
    }
}
