using System.Text;

namespace CodexMulti.Infrastructure;

internal static class TextEncodings
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
