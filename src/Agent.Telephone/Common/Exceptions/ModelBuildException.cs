namespace Agent.Telephone.Common.Exceptions
{
    public class ModelBuildException : Exception
    {
        public ModelBuildException() { }
        public ModelBuildException(string errorMessage) : base(errorMessage)
        { }

    }
}
