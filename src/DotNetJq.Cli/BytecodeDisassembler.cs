using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Cli;

/// <summary>
/// Routes jq's production bytecode disassembler to the CLI host's stdout stream.
/// </summary>
internal static class BytecodeDisassembler
{
    internal static void Write(jq_state state, Stream destination, int indent = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(destination);
        if (state.Bytecode is null)
        {
            throw new InvalidOperationException("Compiled jq bytecode is absent.");
        }

        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1_024,
            leaveOpen: true)
        {
            NewLine = "\n",
        };
        libjq.jq_dump_disassembly(state, indent, writer);
        writer.Flush();
    }
}
