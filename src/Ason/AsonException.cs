namespace Ason;

/// <summary>ASON error type.</summary>
public sealed class AsonException : Exception
{
    public AsonException(string message) : base(message) { }

    public static AsonException Eof => new("unexpected end of input");
    public static AsonException ExpectedColon => new("expected ':'");
    public static AsonException ExpectedOpenParen => new("expected '('");
    public static AsonException ExpectedCloseParen => new("expected ')'");
    public static AsonException ExpectedOpenBrace => new("expected '{'");
    public static AsonException ExpectedCloseBrace => new("expected '}'");
    public static AsonException ExpectedOpenBracket => new("expected '['");
    public static AsonException ExpectedCloseBracket => new("expected ']'");
    public static AsonException ExpectedComma => new("expected ','");
    public static AsonException ExpectedValue => new("expected value");
    public static AsonException TrailingCharacters => new("trailing characters");
    public static AsonException InvalidNumber => new("invalid number");
    public static AsonException UnclosedString => new("unclosed string");
    public static AsonException InvalidUnicodeEscape => new("invalid unicode escape");
    public static AsonException UnsupportedMap => new("map syntax is not supported; use entry-list arrays such as attrs@[{key@str,value@int}]");
}
