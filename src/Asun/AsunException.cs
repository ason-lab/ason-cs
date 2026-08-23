namespace Asun;

/// <summary>ASUN error type.</summary>
public sealed class AsunException : Exception
{
    public AsunException(string message) : base(message) { }

    public static AsunException Eof => new("unexpected end of input");
    public static AsunException ExpectedColon => new("expected ':'");
    public static AsunException ExpectedOpenParen => new("expected '('");
    public static AsunException ExpectedCloseParen => new("expected ')'");
    public static AsunException ExpectedOpenBrace => new("expected '{'");
    public static AsunException ExpectedCloseBrace => new("expected '}'");
    public static AsunException ExpectedOpenBracket => new("expected '['");
    public static AsunException ExpectedCloseBracket => new("expected ']'");
    public static AsunException ExpectedComma => new("expected ','");
    public static AsunException ExpectedValue => new("expected value");
    public static AsunException TrailingCharacters => new("trailing characters");
    public static AsunException InvalidNumber => new("invalid number");
    public static AsunException UnclosedString => new("unclosed string");
    public static AsunException InvalidUnicodeEscape => new("invalid unicode escape");
    public static AsunException UnsupportedMap => new("map syntax is not supported");
    public static AsunException MaxDepthExceeded => new("maximum nesting depth exceeded");
    public static AsunException IntegerOutOfRange => new("integer out of range");
}
