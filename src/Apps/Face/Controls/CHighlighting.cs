#region

using System.Text;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

#endregion

namespace Face.Controls;

public static class CHighlighting {
    private static IHighlightingDefinition? _dark;
    private static IHighlightingDefinition? _light;

    public static IHighlightingDefinition GetDefinition(bool isDark) {
        if (isDark)
            return CHighlighting._dark
                ??= Load(MakeXshd("#6A9955", "#C586C0", "#569CD6", "#9CDCFE", "#CE9178", "#B5CEA8"));
        return CHighlighting._light
            ??= Load(MakeXshd("#5A7238", "#7B3F9A", "#0055CC", "#006B99", "#A31515", "#098658"));
    }

    private static IHighlightingDefinition Load(string xshd) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xshd));
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static string MakeXshd(
        string comment,
        string preprocessor,
        string keyword,
        string type,
        string str,
        string number
    ) => $"""
          <?xml version="1.0"?>
          <SyntaxDefinition name="C" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            <Color name="Comment"      foreground="{comment}" />
            <Color name="Preprocessor" foreground="{preprocessor}" />
            <Color name="Keyword"      foreground="{keyword}" />
            <Color name="Type"         foreground="{type}" />
            <Color name="String"       foreground="{str}" />
            <Color name="Number"       foreground="{number}" />

            <RuleSet>
              <Span color="Comment" begin="//" />
              <Span color="Comment" multiline="true" begin="/\*" end="\*/" />

              <Span color="String" begin="&quot;" end="&quot;">
                <RuleSet>
                  <Span begin="\\" end="." />
                </RuleSet>
              </Span>
              <Span color="String" begin="'" end="'">
                <RuleSet>
                  <Span begin="\\" end="." />
                </RuleSet>
              </Span>

              <Rule color="Preprocessor">^\s*\#\w+</Rule>

              <Keywords color="Keyword">
                <Word>if</Word><Word>else</Word><Word>for</Word><Word>while</Word><Word>do</Word>
                <Word>switch</Word><Word>case</Word><Word>default</Word>
                <Word>break</Word><Word>continue</Word><Word>return</Word><Word>goto</Word>
                <Word>sizeof</Word><Word>typedef</Word>
                <Word>static</Word><Word>extern</Word><Word>register</Word><Word>inline</Word>
                <Word>const</Word><Word>volatile</Word><Word>restrict</Word>
              </Keywords>

              <Keywords color="Type">
                <Word>void</Word><Word>char</Word><Word>short</Word><Word>int</Word><Word>long</Word>
                <Word>float</Word><Word>double</Word><Word>signed</Word><Word>unsigned</Word>
                <Word>struct</Word><Word>union</Word><Word>enum</Word><Word>_Bool</Word>
                <Word>int8_t</Word><Word>int16_t</Word><Word>int32_t</Word><Word>int64_t</Word>
                <Word>uint8_t</Word><Word>uint16_t</Word><Word>uint32_t</Word><Word>uint64_t</Word>
                <Word>size_t</Word><Word>intptr_t</Word><Word>uintptr_t</Word><Word>bool</Word>
              </Keywords>

              <Rule color="Number">0[xX][0-9a-fA-F]+[uUlL]*|\d+(\.\d+)?[uUlLfF]*</Rule>
            </RuleSet>
          </SyntaxDefinition>
          """;
}