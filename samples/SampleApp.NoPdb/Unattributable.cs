namespace SampleApp.NoPdb
{
    /// <summary>Built without a PDB, so no member here can resolve a source line.</summary>
    public sealed class Unattributable
    {
        private readonly int _factor;

        public Unattributable(int factor)
        {
            _factor = factor;
        }

        public int Scale(int value)
        {
            return value * _factor;
        }

        public int Offset(int value)
        {
            return value + _factor;
        }
    }
}
