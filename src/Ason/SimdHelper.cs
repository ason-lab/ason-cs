using System.Buffers;
using System.Runtime.CompilerServices;

namespace Ason;

/// <summary>
/// SIMD-accelerated helpers for scanning special characters in ASON strings.
/// Uses .NET SearchValues which auto-selects SSE2/AVX2/AdvSimd at runtime.
/// </summary>
internal static class SimdHelper
{
    // ASON special chars: , ( ) [ ] { } : " \ \n \r \t
    private static readonly SearchValues<char> s_specialChars =
        SearchValues.Create(",()[]{}:\"\\'\n\r\t");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsAnySpecial(ReadOnlySpan<char> s)
    {
        return s.ContainsAny(s_specialChars);
    }

    /// <summary>
    /// SIMD-accelerated scan for quote or backslash in quoted string parsing.
    /// </summary>
    private static readonly SearchValues<char> s_quoteOrBackslash =
        SearchValues.Create("\"\\");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOfQuoteOrBackslash(ReadOnlySpan<char> s)
    {
        return s.IndexOfAny(s_quoteOrBackslash);
    }

    /// <summary>
    /// Fast whitespace skip: space, tab, \n, \r
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipWhitespace(ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c != ' ' && c != '\t' && c != '\n' && c != '\r') break;
            i++;
        }
        return i;
    }

    /// <summary>
    /// Fast scan for schema delimiter: , } @ space tab
    /// </summary>
    private static readonly SearchValues<char> s_schemaDelimiters =
        SearchValues.Create(",}@\t ");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOfSchemaDelimiter(ReadOnlySpan<char> s)
    {
        return s.IndexOfAny(s_schemaDelimiters);
    }
}
