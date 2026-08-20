namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>
    /// Compiler-recognised marker that enables <c>init</c> accessors on netstandard2.0.
    /// Every <c>init</c> setter in this assembly carries a modreq on this exact type, so it must come
    /// from this assembly and only this assembly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
