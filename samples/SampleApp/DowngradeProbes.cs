using System.Text;

namespace SampleApp
{
    /// <summary>
    /// Four levels deep, with the true origin at the bottom. Every level above it diverges only because
    /// the level below did, so the frontier rule has to blame the deepest one and suppress the rest.
    /// </summary>
    public static class DeepChain
    {
        public static int LevelOne(int n) => LevelTwo(n) + 1;

        public static int LevelTwo(int n) => LevelThree(n) + 1;

        public static int LevelThree(int n) => LevelFour(n) + 1;

        /// <summary>The origin. Its own descendants are unaffected, so it should come out as frontier.</summary>
        public static int LevelFour(int n) => Unaffected(n) * DowngradeConfig.Magnitude;

        private static int Unaffected(int n) => Passthrough(n);

        private static int Passthrough(int n) => n;
    }

    /// <summary>Diverges, with an identical-but-Partial child beneath it.</summary>
    public static class PartialSubtree
    {
        public static int Parent(int n)
        {
            // Identical in both runs, but its digest carries an <error:> marker, so "identical" here is
            // not evidence of identical behavior.
            ErrorProbes.Unreadable(new Wrapper<Unreadable>("beneath", default));
            return n + DowngradeConfig.Magnitude;
        }
    }

    /// <summary>The diverging node's own rendered value is truncated, so it is Partial in itself.</summary>
    public static class PartialSelf
    {
        public static string Build(int n)
        {
            int length = 2400 + (n * DowngradeConfig.Magnitude);
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                builder.Append((char)('a' + (i % 26)));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Diverges, and calls a generic method in its own type. Generic members cannot be patched, so that
    /// call produces no trace event at all - the subtree is incomplete in a way only the manifest knows.
    /// </summary>
    public static class SkippedSubtree
    {
        public static int Parent(int n)
        {
            return Echo(n) + DowngradeConfig.Magnitude;
        }

        public static T Echo<T>(T value) => value;
    }

    /// <summary>
    /// Diverges, and calls into a reflectively-loaded assembly. SampleApp cannot reference that assembly
    /// statically without defeating the point, so the call arrives through a field the test sets rather
    /// than through an argument - a delegate argument would be blocklisted and make this node Partial too.
    /// </summary>
    public static class LatePatchedSubtree
    {
        public static System.Func<decimal, string>? PluginFormat;

        public static int Parent(int n)
        {
            PluginFormat?.Invoke(1m);
            return n + DowngradeConfig.Magnitude;
        }
    }
}
