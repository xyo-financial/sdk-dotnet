using System;

namespace Xyo.Sdk.Internal;

/// <summary>
/// Renders attacker-influenced server response text safe to place in an exception message.
/// </summary>
/// <remarks>
/// <para>
/// <c>Exception.Message</c> is the part of an exception that reliably reaches logs, so anything a remote
/// party can influence must be flattened and clamped before it lands there. Full, unaltered fidelity stays
/// available on the <c>RawResponseBody</c> property of the SDK's exception types, which callers opt into.
/// </para>
/// <para>
/// Shared rather than duplicated per call site: this hardening was originally applied only to
/// <c>XyoClient.SafeSummary</c>, leaving the sibling path through
/// <c>XyoProblemDetailsException.FromJson</c> passing ESC, NUL, U+2028 and lone surrogates straight into a
/// log line. One implementation is the only way the two paths cannot drift again.
/// </para>
/// </remarks>
internal static class LogSafeText
{
    /// <summary>
    /// Default clamp applied to a response body before it is placed in an exception message.
    /// </summary>
    internal const int DefaultMaxLength = 512;

    /// <summary>
    /// Flattens then clamps <paramref name="raw"/> for safe inclusion in an exception message.
    /// </summary>
    internal static string Summarize(string raw, int maxLength = DefaultMaxLength) =>
        Truncate(FlattenControlCharacters(raw), maxLength);

    /// <summary>
    /// Replaces every control character with a space, plus the two Unicode line separators
    /// <c>char.IsControl</c> does not cover.
    /// </summary>
    /// <remarks>
    /// CR and LF alone are not enough: U+2028 and U+2029 are line separators to most JavaScript-based log
    /// viewers, ESC enables ANSI escape injection into a terminal-rendered log, and NUL truncates a message
    /// in C-based sinks. All four are log-line forgery primitives (CWE-117).
    /// </remarks>
    internal static string FlattenControlCharacters(string raw) =>
        string.Create(raw.Length, raw, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                span[i] = (char.IsControl(c) || c == '\u2028' || c == '\u2029') ? ' ' : c;
            }
        });

    /// <summary>
    /// Truncates to at most <paramref name="maxLength"/> UTF-16 code units without splitting a surrogate
    /// pair, which a naive <c>value[..maxLength]</c> can do and which some structured-log serializers reject
    /// outright as invalid UTF-16.
    /// </summary>
    internal static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }
}
