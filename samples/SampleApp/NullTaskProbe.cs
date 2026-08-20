using System.Threading.Tasks;

namespace SampleApp
{
    /// <summary>
    /// A non-async method declared to return Task that returns null. This is the only shape that reaches
    /// AttachContinuation with a null task, and it is the shape that would double-emit if the frame's
    /// emit claim were not honoured.
    /// </summary>
    public sealed class NullTaskProbe
    {
        public Task? NeverStarted()
        {
            return null;
        }

        public Task<int>? NeverStartedOfT()
        {
            return null;
        }
    }
}
