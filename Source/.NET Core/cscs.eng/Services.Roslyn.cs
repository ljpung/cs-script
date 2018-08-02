﻿using csscript;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CSScripting.CodeDom
{
    static class RoslynService
    {

        static (string file, int line) Translate(this Dictionary<(int, int), (string, int)> mapping, int line)
        {
            foreach ((int start, int end) range in mapping.Keys)
                if (range.start <= line && line <= range.end)
                {
                    (string file, int lineOffset) = mapping[range];
                    return (file, line - range.start + lineOffset);
                }

            return ("", 0);
        }

        static string[] SeparateUsingsFromCode(this string code)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
            int pos = root.Usings.FullSpan.End;

            return new[] { code.Substring(0, pos).TrimEnd(), code.Substring(pos) };
        }

        public static CompilerResults CompileAssemblyFromFileBatch_with_roslyn(CompilerParameters options, string[] fileNames)
        {
            // setting up build folder
            string projectName = fileNames.First().GetFileName();

            var engine_dir = typeof(RoslynService).Assembly.Location.GetDirName();
            var cache_dir = CSExecutor.ScriptCacheDir; // C:\Users\user\AppData\Local\Temp\csscript.core\cache\1822444284
            var build_dir = cache_dir.PathJoin(".build", projectName);

            build_dir.DeleteDir()
                     .EnsureDir();

            string firstScript = fileNames.First();
            string attr_file = fileNames.FirstOrDefault(x => x.EndsWith(".attr.g.cs", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".attr.g.vb", StringComparison.OrdinalIgnoreCase));
            string dbg_inject_file = fileNames.FirstOrDefault(x => x.GetFileName().StartsWith("dbg.inject.", StringComparison.OrdinalIgnoreCase));

            string single_source = build_dir.PathJoin(firstScript.GetFileName());

            if (attr_file != null)
            {
                // scripting does not support attributes
                // (0,2): error CS7026: Assembly and module attributes are not allowed in this context
                // writer.WriteLine(File.ReadAllText(attr_file));
            }


            // As per dotnet.exe v2.1.26216.3 the pdb get generated as PortablePDB, which is the only format that is supported 
            // by both .NET debugger (VS) and .NET Core debugger (VSCode).

            // However PortablePDB does not store the full source path but file name only (at least for now). It works fine in typical
            // .Core scenario where the all sources are in the root directory but if they are not (e.g. scripting or desktop app) then
            // debugger cannot resolve sources without user input.

            // The only solution (ugly one) is to inject the full file path at startup with #line directive

            // merge all scripts into a single source
            //    move all scripts' usings to the file header
            //    append the first script whole content
            //    append all imported scripts bodies at the bottom of the first script
            //    ensure all scripts' content is separated by debugger directive `#line...`

            var importedSources = new Dictionary<string, (int, string[])>(); // file, usings count, code lines

            var combinedScript = new List<string>();

            // exclude dbg_inject_file because it has extension methods, which are not permitted in Roslyn scripts 
            // exclude attr_file because it has assembly attribute, which is not permitted in Roslyn scripts 
            var imported_sources = fileNames.Where(x => x != attr_file && x != firstScript && x != dbg_inject_file);

            var mapping = new Dictionary<(int, int), (string, int)>();

            foreach (string file in imported_sources)
            {
                var parts = File.ReadAllText(file).SeparateUsingsFromCode();
                var usings = parts[0].GetLines();
                var code = parts[1].GetLines();

                importedSources[file] = (usings.Count(), code);
                add_code(file, usings, 0);
            }

            void add_code(string file, string[] codeLines, int lineOffset)
            {
                int start = combinedScript.Count;
                combinedScript.AddRange(codeLines);
                int end = combinedScript.Count;
                mapping[(start, end)] = (file, lineOffset);
            }

            combinedScript.Add($"#line 1 \"{firstScript}\"");
            add_code(firstScript, File.ReadAllLines(firstScript), 0);

            foreach (string file in importedSources.Keys)
            {
                (var usings_count, var code) = importedSources[file];

                combinedScript.Add($"#line {usings_count + 1} \"{file}\"");
                add_code(file, code, usings_count);
            }

            File.WriteAllLines(single_source, combinedScript.ToArray());

            // prepare for compiling
            var ref_assemblies = options.ReferencedAssemblies.Where(x => !x.IsSharedAssembly())
                                                             .Where(Path.IsPathRooted)
                                                             .Where(asm => asm.GetDirName() != engine_dir)
                                                             .ToArray();

            var refs = new StringBuilder();
            var assembly = build_dir.PathJoin(projectName + ".dll");

            var result = new CompilerResults();

            //pseudo-gac as .NET core does not support GAC but rather common assemblies.
            var gac = typeof(string).Assembly.Location.GetDirName();

            Profiler.get("compiler").Restart();

            //----------------------------

            var scriptText = File.ReadAllText(single_source);
            ScriptOptions CompilerSettings = ScriptOptions.Default;

            foreach (string file in Directory.GetFiles(gac, "System.*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(file);
                    CompilerSettings = CompilerSettings.AddReferences(asm);
                }
                catch
                {
                }
            }

            foreach (string file in ref_assemblies)
            {
                var asm = Assembly.LoadFrom(file);
                CompilerSettings = CompilerSettings.AddReferences(asm);
            }

            var compilation = CSharpScript.Create(scriptText, CompilerSettings)
                                          .GetCompilation();

            if (options.IncludeDebugInformation)
                compilation = compilation.WithOptions(compilation.Options
                                         .WithOptimizationLevel(OptimizationLevel.Debug)
                                         .WithOutputKind(OutputKind.DynamicallyLinkedLibrary));

            using (var pdb = new MemoryStream())
            using (var asm = new MemoryStream())
            {
                var emitOptions = new EmitOptions(false, DebugInformationFormat.PortablePdb);

                EmitResult emitResult;
                if (options.IncludeDebugInformation)
                    emitResult = compilation.Emit(asm, pdb, options: emitOptions);
                else
                    emitResult = compilation.Emit(asm);

                if (!emitResult.Success)
                {
                    IEnumerable<Diagnostic> failures = emitResult.Diagnostics.Where(d => d.IsWarningAsError ||
                                                                                         d.Severity == DiagnosticSeverity.Error);

                    var message = new StringBuilder();
                    foreach (Diagnostic diagnostic in failures)
                    {
                        string error_location = "";
                        if (diagnostic.Location.IsInSource)
                        {
                            var error_pos = diagnostic.Location.GetLineSpan().StartLinePosition;

                            int error_line = error_pos.Line;
                            int error_column = error_pos.Character;

                            var source = diagnostic.Location.SourceTree.FilePath;
                            if (source == "")
                            {
                                (source, error_line) = mapping.Translate(error_line);
                            }

                            error_line++; 
                            error_location = $"{source}({error_line},{ error_column}): ";
                        }
                        message.AppendLine($"{error_location}error {diagnostic.Id}: {diagnostic.GetMessage()}");
                    }
                    var errors = message.ToString();
                    throw new CompilerException(errors);
                }
                else
                {
                    asm.Seek(0, SeekOrigin.Begin);
                    byte[] buffer = asm.GetBuffer();

                    File.WriteAllBytes(assembly, buffer);

                    if (options.IncludeDebugInformation)
                    {
                        pdb.Seek(0, SeekOrigin.Begin);
                        byte[] pdbBuffer = pdb.GetBuffer();

                        File.WriteAllBytes(assembly.ChangeExtension(".pdb"), pdbBuffer);
                    }
                }
            }

            //----------------------------
            Profiler.get("compiler").Stop();

            Console.WriteLine("    roslyn: " + Profiler.get("compiler").Elapsed);

            result.ProcessErrors();

            result.Errors
                  .ForEach(x =>
                  {
                      // by default x.FileName is a file name only 
                      x.FileName = fileNames.FirstOrDefault(f => f.EndsWith(x.FileName ?? "")) ?? x.FileName;
                  });

            if (result.NativeCompilerReturnValue == 0 && File.Exists(assembly))
            {
                result.PathToAssembly = options.OutputAssembly;
                File.Copy(assembly, result.PathToAssembly, true);

                if (options.IncludeDebugInformation)
                    File.Copy(assembly.ChangeExtension(".pdb"),
                              result.PathToAssembly.ChangeExtension(".pdb"),
                              true);
            }
            else
            {
                if (result.Errors.IsEmpty())
                {
                    // unknown error; e.g. invalid compiler params 
                    result.Errors.Add(new CompilerError { ErrorText = "Unknown compiler error" });
                }
            }

            build_dir.DeleteDir();

            return result;
        }

    }
}