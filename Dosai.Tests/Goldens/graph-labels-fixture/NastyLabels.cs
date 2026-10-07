using System;
using System.Diagnostics;

namespace Ünïcödé.日本語;

public static class NastyLabels
{
    // The cli source pattern binds an entry point's `args`; the nasty literal rides along in
    // the node/slice Code text so every exporter escapes path (mermaid quotes, XML entities,
    // raw UTF-8) is exercised by the committed goldens.
    static void Main(string[] args)
    {
        // The invocation spans lines on purpose: the operation's Code text then carries real
        // newlines and tab indentation, which the mermaid escaper must blank and XML must pass.
        Process.Start(
			args[0] + "quote\" apostrophe' lt< gt> amp& pipe| bracket[in] paren(in) brace{in} hash# semi; emoji😀 déjà vu",
			"");
    }
}
