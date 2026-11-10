using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace DebugLogContextWeaver.CodeGen
{
    internal sealed partial class DebugLogContextILPostProcessor : ILPostProcessor
    {
        private static readonly string SelfAssemblyName  = typeof(DebugLogContextILPostProcessor).Assembly.GetName().Name;
        private static readonly string DebugTypeFullName = typeof(UnityEngine.Debug).FullName;
        private static readonly string UnityObjectName   = typeof(UnityEngine.Object).FullName;
        private static readonly string CompilerGenerated = typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).FullName;

        private static readonly string[] EngineAssemblyNames =
        {
            typeof(UnityEngine.Debug).Assembly.GetName().Name, // "UnityEngine.CoreModule"
            nameof(UnityEngine)
        };

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            if (compiledAssembly.Name == SelfAssemblyName)
            {
                return false;
            }

            var inMemory = compiledAssembly.InMemoryAssembly;

            if (inMemory?.PeData == null || inMemory.PdbData == null)
            {
                // NOTE:
                // Non-null by Unity's contract, but checked defensively since the implementation is external.
                return false;
            }

            foreach (var reference in compiledAssembly.References)
            {
                var name = Path.GetFileNameWithoutExtension(reference);

                if (0 <= Array.IndexOf(EngineAssemblyNames, name))
                {
                    return true;
                }
            }

            return false;
        }

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            var diagnostics = new List<DiagnosticMessage>();

            ReferenceAssemblyResolver resolver = null;
            AssemblyDefinition        assembly = null;

            try
            {
                resolver = new ReferenceAssemblyResolver(compiledAssembly.References);
                assembly = ReadAssembly(compiledAssembly, resolver);
                resolver.Self = assembly;

                var rewritten = new Rewriter(assembly.MainModule).Run();

                if (rewritten == 0)
                {
                    return new ILPostProcessResult(null!, diagnostics);
                }

                var pe  = new MemoryStream();
                var pdb = new MemoryStream();

                assembly.Write(pe, new WriterParameters
                {
                    SymbolWriterProvider = new PortablePdbWriterProvider(),
                    SymbolStream         = pdb,
                    WriteSymbols         = true,
                });

                return new ILPostProcessResult(new InMemoryAssembly(pe.ToArray(), pdb.ToArray()), diagnostics);
            }
            catch (Exception e)
            {
                diagnostics.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Warning,
                    MessageData    = $"[{SelfAssemblyName}] Skipped '{compiledAssembly.Name}': {e}",
                });

                return new ILPostProcessResult(null!, diagnostics);
            }
            finally
            {
                assembly?.Dispose();
                resolver?.Dispose();
            }
        }

        private static AssemblyDefinition ReadAssembly(ICompiledAssembly compiledAssembly, IAssemblyResolver resolver)
        {
            var readerParameters = new ReaderParameters
            {
                AssemblyResolver     = resolver,
                ReadSymbols          = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream         = new MemoryStream(compiledAssembly.InMemoryAssembly.PdbData),
                ReadingMode          = ReadingMode.Immediate,
            };

            var peStream = new MemoryStream(compiledAssembly.InMemoryAssembly.PeData);

            return AssemblyDefinition.ReadAssembly(peStream, readerParameters);
        }
    }
}