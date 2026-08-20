using System;

namespace SampleApp
{
    /// <summary>
    /// A field of this type cannot be read reflectively: boxing it forces the type initializer, which
    /// throws, and the CLR caches the failure so every later read throws the same way.
    /// </summary>
    /// <remarks>
    /// Chosen because normal code is unaffected - constructing, copying and passing the struct never runs
    /// the initializer, so the app under test behaves identically whether or not the tracer is attached.
    /// Only the reflective read fails, which is exactly the condition being exercised.
    /// </remarks>
    public struct Unreadable
    {
        public int Slot;

        static Unreadable() => throw new InvalidOperationException("field reads of this type must fail");
    }

    /// <summary>Second unreadable shape, used to show what the error marker still cannot distinguish.</summary>
    public struct AlsoUnreadable
    {
        public int Slot;

        static AlsoUnreadable() => throw new InvalidOperationException("field reads of this type must fail");
    }

    /// <summary>
    /// Generic so the readable and unreadable cases render the same type name and the same field names,
    /// leaving the payload as the only difference between their digests.
    /// </summary>
    public sealed class Wrapper<T>
    {
        private readonly string _label;
        private readonly T _payload;

        public Wrapper(string label, T payload)
        {
            _label = label;
            _payload = payload;
        }
    }

    public static class ErrorProbes
    {
        public static int Readable(Wrapper<int> wrapper)
        {
            return wrapper is null ? 0 : 1;
        }

        public static int Unreadable(Wrapper<Unreadable> wrapper)
        {
            return wrapper is null ? 0 : 1;
        }

        public static int UnreadableOther(Wrapper<AlsoUnreadable> wrapper)
        {
            return wrapper is null ? 0 : 1;
        }
    }
}
