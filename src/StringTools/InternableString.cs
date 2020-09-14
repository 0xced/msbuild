﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace StringTools
{
    /// <summary>
    /// Represents a string that can be converted to System.String with interning, i.e. by returning an existing string if it has been seen before
    /// and is still tracked in the intern table.
    /// </summary>
    /// <remarks>
    /// The structure is public because it's exposed via the <see cref="TryInternStringDelegate"/> callback. It's a mutable struct and should
    /// generally not be used directly from outside of the library.
    /// </remarks>
    public ref struct InternableString
    {
        /// <summary>
        /// Enumerator for the top-level struct. Enumerates characters of the string.
        /// </summary>
        public ref struct Enumerator
        {
            /// <summary>
            /// The InternableString being enumerated.
            /// </summary>
            private InternableString _string;

            /// <summary>
            /// Index of the current span, -1 represents the inline span.
            /// </summary>
            private int _spanIndex;

            /// <summary>
            /// Index of the current character in the current span, -1 if MoveNext has not been called yet.
            /// </summary>
            private int _charIndex;

            internal Enumerator(ref InternableString str)
            {
                _string = str;
                _spanIndex = -1;
                _charIndex = -1;
            }

            /// <summary>
            /// Returns the current character.
            /// </summary>
            public ref readonly char Current
            {
                get
                {
                    if (_spanIndex == -1)
                    {
                        return ref _string._inlineSpan[_charIndex];
                    }
                    ReadOnlyMemory<char> span = _string._spans[_spanIndex];
                    return ref span.Span[_charIndex];
                }
            }

            /// <summary>
            /// Moves to the next character.
            /// </summary>
            /// <returns>True if there is another character, false if the enumerator reached the end.</returns>
            public bool MoveNext()
            {
                int newCharIndex = _charIndex + 1;
                if (_spanIndex == -1)
                {
                    if (newCharIndex < _string._inlineSpan.Length)
                    {
                        _charIndex = newCharIndex;
                        return true;
                    }
                    _spanIndex = 0;
                    newCharIndex = 0;
                }

                if (_string._spans != null)
                {
                    while (_spanIndex < _string._spans.Count)
                    {
                        if (newCharIndex < _string._spans[_spanIndex].Length)
                        {
                            _charIndex = newCharIndex;
                            return true;
                        }
                        _spanIndex++;
                        newCharIndex = 0;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// The span held by this struct, inline to be able to represent ReadOnlySpan&lt;char&gt;. May be empty.
        /// </summary>
        private ReadOnlySpan<char> _inlineSpan;

#if NETSTANDARD
        /// <summary>
        /// .NET Core does not keep a reference to the containing object in ReadOnlySpan&lt;T&gt;. In particular, it cannot recover
        /// the string if the span represents one. We have to hold the reference separately to be able to roundtrip
        /// String-&gt;InternableString-&gt;String without allocating a new String.
        /// </summary>
        private string _inlineSpanString;
#endif

        /// <summary>
        /// Additional spans held by this struct. May be null.
        /// </summary>
        private List<ReadOnlyMemory<char>> _spans;

        /// <summary>
        /// Constructs a new InternableString wrapping the given ReadOnlySpan&lt;char&gt;.
        /// </summary>
        /// <param name="span">The span to wrap.</param>
        /// <remarks>
        /// When wrapping a span representing an entire System.String, use Internable(string) for optimum performance.
        /// </remarks>
        internal InternableString(ReadOnlySpan<char> span)
        {
            _inlineSpan = span;
            _spans = null;
            Length = span.Length;
#if NETSTANDARD
            _inlineSpanString = null;
#endif
        }

        /// <summary>
        /// Constructs a new InternableString wrapping the given string.
        /// </summary>
        /// <param name="str">The string to wrap, must be non-null.</param>
        internal InternableString(string str)
        {
            if (str == null)
            {
                throw new ArgumentNullException(nameof(str));
            }

            _inlineSpan = str.AsSpan();
            _spans = null;
            Length = str.Length;
#if NETSTANDARD
            _inlineSpanString = str;
#endif
        }

        /// <summary>
        /// Constructs a new InternableString wrapping the given SpanBasedStringBuilder.
        /// </summary>
        internal InternableString(SpanBasedStringBuilder stringBuilder)
        {
            _inlineSpan = default(ReadOnlySpan<char>);
            _spans = stringBuilder.Spans;
            Length = stringBuilder.Length;
#if NETSTANDARD
            _inlineSpanString = null;
#endif
        }

        /// <summary>
        /// Gets the length of the string.
        /// </summary>
        public int Length { get; private set; }

        /// <summary>
        /// Creates a new enumerator for enumerating characters in this string. Does not allocate.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(ref this);
        }

        /// <summary>
        /// Returns the character at the given index.
        /// </summary>
        /// <param name="index">The index to return the character at.</param>
        /// <returns>The character.</returns>
        /// <remarks>
        /// Similar to StringBuilder, this indexer does not work in constant time and may take O(N) where N is the index
        /// in the worst case. Use <see cref="GetEnumerator"/> for scanning the string in a linear fashion.
        /// </remarks>
        public char this[int index]
        {
            get
            {
                if (index < 0 || index >= Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                if (index < _inlineSpan.Length)
                {
                    return _inlineSpan[index];
                }
                index -= _inlineSpan.Length;

                foreach (ReadOnlyMemory<char> span in _spans)
                {
                    if (index < span.Length)
                    {
                        return span.Span[index];
                    }
                    index -= span.Length;
                }

                throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// Returns true if the string starts with another string.
        /// </summary>
        /// <param name="other">Another string.</param>
        /// <returns>True if this string starts with <paramref name="other"/>.</returns>
        public bool StartsWithStringByOrdinalComparison(string other)
        {
            int otherLength = other.Length;
            if (otherLength > Length)
            {
                // Can't start with a string which is longer.
                return false;
            }

            if (otherLength <= _inlineSpan.Length)
            {
                return _inlineSpan.StartsWith(other.AsSpan());
            }
            if (_inlineSpan.CompareTo(other.AsSpan(0, _inlineSpan.Length), StringComparison.Ordinal) != 0)
            {
                return false;
            }

            int otherStart = _inlineSpan.Length;
            foreach (ReadOnlyMemory<char> span in _spans)
            {
                if (otherLength - otherStart <= span.Length)
                {
                    return span.Span.StartsWith(other.AsSpan(otherStart, otherLength - otherStart));
                }
                if (span.Span.CompareTo(other.AsSpan(otherStart, span.Length), StringComparison.Ordinal) != 0)
                {
                    return false;
                }
                otherStart += span.Length;
            }

            throw new InvalidOperationException();
        }

        /// <summary>
        /// Returns a System.String representing this string. Allocates memory unless this InternableString was created by wrapping a
        /// System.String in which case the original string is returned.
        /// </summary>
        /// <returns>The string.</returns>
        public unsafe string ExpensiveConvertToString()
        {
            if (Length == 0)
            {
                return string.Empty;
            }

            // Special case: if we hold just one string, we can directly return it.
            if (_inlineSpan.Length == Length)
            {
#if NETSTANDARD
                if (_inlineSpanString != null)
                {
                    return _inlineSpanString;
                }
#else
                return _inlineSpan.ToString();
#endif
            }
            if (_inlineSpan.IsEmpty && _spans[0].Length == Length)
            {
                return _spans[0].ToString();
            }

            // In all other cases we create a new string instance and concatenate all spans into it. Note that while technically mutating
            // the System.String, the technique is generally considered safe as we are the sole owners of the new object. It is important
            // to initialize the string with the '\0' characters as this hits an optimized code path in the runtime.
            string result = new string((char)0, Length);
            int charsRemaining = Length;

            fixed (char* resultPtr = result)
            {
                char* destPtr = resultPtr;
                if (_inlineSpan.Length > 0)
                {
                    _inlineSpan.CopyTo(new Span<char>(destPtr, charsRemaining));
                    destPtr += _inlineSpan.Length;
                    charsRemaining -= _inlineSpan.Length;
                }

                if (_spans != null)
                {
                    foreach (ReadOnlyMemory<char> span in _spans)
                    {
                        span.Span.CopyTo(new Span<char>(destPtr, charsRemaining));
                        destPtr += span.Length;
                        charsRemaining -= span.Length;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Returns true if this InternableString wraps a System.String and the same System.String is passed as the argument.
        /// </summary>
        /// <param name="str">The string to compare to.</param>
        /// <returns>True is this instance wraps the given string.</returns>
        public bool ReferenceEquals(string str)
        {
            if (_inlineSpan.Length == Length)
            {
                return _inlineSpan == str.AsSpan();
            }
            if (_inlineSpan.IsEmpty && _spans.Count == 1 && _spans[0].Length == Length)
            {
                return _spans[0].Span == str.AsSpan();
            }
            return false;
        }

        /// <summary>
        /// Converts this instance to a System.String while first searching for a match in the intern table.
        /// </summary>
        /// <remarks>
        /// May allocate depending on whether the string has already been interned.
        /// </remarks>
        public override unsafe string ToString()
        {
            return OpportunisticIntern.Instance.InternableToString(ref this);
        }
    }
}
