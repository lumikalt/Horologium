#region

using System.Text;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

#endregion

namespace Face.Controls;

public static class RvHighlighting {
    private static IHighlightingDefinition? _dark;
    private static IHighlightingDefinition? _light;

    public static IHighlightingDefinition GetDefinition(bool isDark) {
        if (isDark)
            return RvHighlighting._dark
                ??= Load(MakeXshd("#6A9955", "#C586C0", "#569CD6", "#9CDCFE", "#DCDCAA", "#B5CEA8"));
        return RvHighlighting._light
            ??= Load(MakeXshd("#5A7238", "#7B3F9A", "#0055CC", "#006B99", "#795E26", "#098658"));
    }

    private static IHighlightingDefinition Load(string xshd) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xshd));
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static string MakeXshd(
        string comment,
        string directive,
        string opcode,
        string register,
        string label,
        string number
    ) => $"""
          <?xml version="1.0"?>
          <SyntaxDefinition name="RISC-V" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            <Color name="Comment"   foreground="{comment}" />
            <Color name="Directive" foreground="{directive}" />
            <Color name="Opcode"    foreground="{opcode}" />
            <Color name="Register"  foreground="{register}" />
            <Color name="Label"     foreground="{label}" />
            <Color name="Number"    foreground="{number}" />

            <RuleSet>
              <Rule color="Comment">[#][^\r\n]*</Rule>

              <Rule color="Directive">\.[a-zA-Z_][a-zA-Z0-9_]*</Rule>

              <Keywords color="Opcode">
                <Word>add</Word><Word>sub</Word><Word>xor</Word><Word>or</Word><Word>and</Word>
                <Word>sll</Word><Word>srl</Word><Word>sra</Word><Word>slt</Word><Word>sltu</Word>
                <Word>addi</Word><Word>xori</Word><Word>ori</Word><Word>andi</Word>
                <Word>slli</Word><Word>srli</Word><Word>srai</Word><Word>slti</Word><Word>sltiu</Word>
                <Word>lb</Word><Word>lh</Word><Word>lw</Word><Word>lbu</Word><Word>lhu</Word>
                <Word>sb</Word><Word>sh</Word><Word>sw</Word>
                <Word>lui</Word><Word>auipc</Word>
                <Word>jal</Word><Word>jalr</Word>
                <Word>beq</Word><Word>bne</Word><Word>blt</Word><Word>bge</Word><Word>bltu</Word><Word>bgeu</Word>
                <Word>mul</Word><Word>mulh</Word><Word>mulhsu</Word><Word>mulhu</Word>
                <Word>div</Word><Word>divu</Word><Word>rem</Word><Word>remu</Word>
                <Word>lr.w</Word><Word>sc.w</Word>
                <Word>amoswap.w</Word><Word>amoadd.w</Word><Word>amoxor.w</Word>
                <Word>amoand.w</Word><Word>amoor.w</Word>
                <Word>amomin.w</Word><Word>amomax.w</Word><Word>amominu.w</Word><Word>amomaxu.w</Word>
                <Word>flw</Word><Word>fsw</Word>
                <Word>fadd.s</Word><Word>fsub.s</Word><Word>fmul.s</Word><Word>fdiv.s</Word>
                <Word>fsqrt.s</Word><Word>fmin.s</Word><Word>fmax.s</Word>
                <Word>fmadd.s</Word><Word>fmsub.s</Word><Word>fnmadd.s</Word><Word>fnmsub.s</Word>
                <Word>fcvt.w.s</Word><Word>fcvt.wu.s</Word><Word>fcvt.s.w</Word><Word>fcvt.s.wu</Word>
                <Word>fmv.x.w</Word><Word>fmv.w.x</Word>
                <Word>feq.s</Word><Word>flt.s</Word><Word>fle.s</Word><Word>fclass.s</Word>
                <Word>fsgnj.s</Word><Word>fsgnjn.s</Word><Word>fsgnjx.s</Word>
                <Word>ecall</Word><Word>ebreak</Word><Word>fence</Word><Word>fence.i</Word>
                <Word>csrrw</Word><Word>csrrs</Word><Word>csrrc</Word>
                <Word>csrrwi</Word><Word>csrrsi</Word><Word>csrrci</Word>
                <Word>li</Word><Word>la</Word><Word>mv</Word><Word>ret</Word><Word>nop</Word>
                <Word>not</Word><Word>neg</Word>
                <Word>beqz</Word><Word>bnez</Word><Word>blez</Word><Word>bgez</Word><Word>bltz</Word><Word>bgtz</Word>
                <Word>bgt</Word><Word>ble</Word><Word>bgtu</Word><Word>bleu</Word>
                <Word>j</Word><Word>jr</Word><Word>call</Word><Word>tail</Word>
                <Word>seqz</Word><Word>snez</Word><Word>sltz</Word><Word>sgtz</Word>
              </Keywords>

              <Rule color="Label">[a-zA-Z_.][a-zA-Z0-9_.]*\s*:</Rule>

              <Keywords color="Register">
                <Word>x0</Word><Word>x1</Word><Word>x2</Word><Word>x3</Word><Word>x4</Word>
                <Word>x5</Word><Word>x6</Word><Word>x7</Word><Word>x8</Word><Word>x9</Word>
                <Word>x10</Word><Word>x11</Word><Word>x12</Word><Word>x13</Word><Word>x14</Word>
                <Word>x15</Word><Word>x16</Word><Word>x17</Word><Word>x18</Word><Word>x19</Word>
                <Word>x20</Word><Word>x21</Word><Word>x22</Word><Word>x23</Word><Word>x24</Word>
                <Word>x25</Word><Word>x26</Word><Word>x27</Word><Word>x28</Word><Word>x29</Word>
                <Word>x30</Word><Word>x31</Word>
                <Word>f0</Word><Word>f1</Word><Word>f2</Word><Word>f3</Word><Word>f4</Word>
                <Word>f5</Word><Word>f6</Word><Word>f7</Word><Word>f8</Word><Word>f9</Word>
                <Word>f10</Word><Word>f11</Word><Word>f12</Word><Word>f13</Word><Word>f14</Word>
                <Word>f15</Word><Word>f16</Word><Word>f17</Word><Word>f18</Word><Word>f19</Word>
                <Word>f20</Word><Word>f21</Word><Word>f22</Word><Word>f23</Word><Word>f24</Word>
                <Word>f25</Word><Word>f26</Word><Word>f27</Word><Word>f28</Word><Word>f29</Word>
                <Word>f30</Word><Word>f31</Word>
                <Word>zero</Word><Word>ra</Word><Word>sp</Word><Word>gp</Word><Word>tp</Word>
                <Word>t0</Word><Word>t1</Word><Word>t2</Word><Word>t3</Word><Word>t4</Word><Word>t5</Word><Word>t6</Word>
                <Word>s0</Word><Word>s1</Word><Word>s2</Word><Word>s3</Word><Word>s4</Word><Word>s5</Word>
                <Word>s6</Word><Word>s7</Word><Word>s8</Word><Word>s9</Word><Word>s10</Word><Word>s11</Word>
                <Word>a0</Word><Word>a1</Word><Word>a2</Word><Word>a3</Word><Word>a4</Word><Word>a5</Word>
                <Word>a6</Word><Word>a7</Word>
                <Word>ft0</Word><Word>ft1</Word><Word>ft2</Word><Word>ft3</Word><Word>ft4</Word>
                <Word>ft5</Word><Word>ft6</Word><Word>ft7</Word><Word>ft8</Word><Word>ft9</Word>
                <Word>ft10</Word><Word>ft11</Word>
                <Word>fs0</Word><Word>fs1</Word><Word>fs2</Word><Word>fs3</Word><Word>fs4</Word><Word>fs5</Word>
                <Word>fs6</Word><Word>fs7</Word><Word>fs8</Word><Word>fs9</Word><Word>fs10</Word><Word>fs11</Word>
                <Word>fa0</Word><Word>fa1</Word><Word>fa2</Word><Word>fa3</Word><Word>fa4</Word><Word>fa5</Word>
                <Word>fa6</Word><Word>fa7</Word>
              </Keywords>

              <Rule color="Number">0x[0-9a-fA-F]+|[0-9]+</Rule>
            </RuleSet>
          </SyntaxDefinition>
          """;
}