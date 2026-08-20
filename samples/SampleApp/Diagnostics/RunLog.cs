using System.Collections.Generic;

namespace SampleApp.Diagnostics
{
    /// <summary>
    /// Lives under an include prefix but is meant to be excluded by configuration, to show that an
    /// exclusion still leaves a manifest trail.
    /// </summary>
    public sealed class RunLog
    {
        private readonly List<string> _lines = new List<string>();

        public void Record(string message)
        {
            _lines.Add(message);
        }

        public int LineCount()
        {
            return _lines.Count;
        }
    }
}
