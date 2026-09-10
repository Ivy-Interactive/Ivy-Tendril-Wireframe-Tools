using System.Text;
using Ivy.Tendril.Wireframe.Console;

// agent-readme and --print-url are piped into files and other tools, and the reference
// contains non-ASCII (middots, em dashes) as does esbuild's diagnostic output. Without
// this the Windows console transcodes to the OEM codepage and the result is not valid
// UTF-8, which corrupts anything reading it back.
System.Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return CliApp.Build().Run(args);
