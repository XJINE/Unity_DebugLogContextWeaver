using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace DebugLogContextWeaver.CodeGen {
internal sealed partial class DebugLogContextILPostProcessor : ILPostProcessor
{
    private sealed class Rewriter
    {
        private struct Candidate
        {
            public MethodReference Target;       // null: no 'context' overload for this callee.
            public int             ContextIndex; // parameter index where the 'context' must be inserted.
        }

        private struct CallSite
        {
            public Instruction     Call;
            public MethodReference Callee;
            public Candidate       Candidate;
        }

        private readonly ModuleDefinition              _module;
        private readonly Dictionary<string, bool>      _isUnityObjectByTypeName = new ();
        private readonly Dictionary<string, Candidate> _candidateByCalleeName   = new ();

        private TypeDefinition _debugType;
        private bool           _debugTypeResolved;

        public Rewriter(ModuleDefinition module)
        {
            _module = module;
        }

        public int Run()
        {
            var processedCount = 0;

            foreach (var type in _module.GetAllTypes())
            {
                if (!IsProcessType(type))
                {
                    continue;
                }

                foreach (var method in type.Methods)
                {
                    if (method.IsStatic || !method.HasBody)
                    {
                        continue;
                    }

                    processedCount += ProcessMethod(method);
                }
            }

            return processedCount;
        }

        private bool IsProcessType(TypeDefinition type)
        {
            if (!type.IsClass || type.IsValueType || type.IsInterface)
            {
                return false;
            }

            if (IsCompilerGenerated(type))
            {
                return false;
            }

            return IsUnityObject(type);
        }

        private static bool IsCompilerGenerated(TypeDefinition type)
        {
            if (type.Name.StartsWith("<", StringComparison.Ordinal))
            {
                return true;
            }

            if (!type.HasCustomAttributes)
            {
                return false;
            }

            foreach (var attribute in type.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == CompilerGenerated)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsUnityObject(TypeDefinition type)
        {
            if (_isUnityObjectByTypeName.TryGetValue(type.FullName, out var cached))
            {
                return cached;
            }

            var result  = false;
            var current = type.BaseType;

            while (current != null)
            {
                if (current.FullName == UnityObjectName)
                {
                    result = true;
                    break;
                }

                TypeDefinition definition;

                try
                {
                    definition = current.Resolve();
                }
                catch (AssemblyResolutionException)
                {
                    definition = null;
                }

                if (definition == null)
                {
                    break;
                }

                current = definition.BaseType;
            }

            _isUnityObjectByTypeName[type.FullName] = result;

            return result;
        }

        private int ProcessMethod(MethodDefinition method)
        {
            List<CallSite> sites = null;

            // NOTE:
            // Collect first, must not mutate the instruction list while enumerating it.

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code != Code.Call)
                {
                    continue;
                }

                if (instruction.Operand is not MethodReference callee)
                {
                    continue;
                }

                if (callee.HasThis || callee.DeclaringType.FullName != DebugTypeFullName)
                {
                    continue;
                }

                var candidate = FindContextOverload(callee);

                if (candidate.Target == null)
                {
                    continue;
                }

                sites ??= new List<CallSite>();
                sites.Add(new CallSite
                {
                    Call      = instruction,
                    Callee    = callee,
                    Candidate = candidate
                });
            }

            if (sites == null)
            {
                return 0;
            }

            // NOTE:
            // Expand short-form instructions to their long forms with 'SimplifyMacros'.
            // Inserting instructions may push short branches out of range.

            var body = method.Body;
                body.SimplifyMacros();
                body.InitLocals = true;

            var il = body.GetILProcessor();

            foreach (var site in sites)
            {
                RewriteCall(il, body, site);
            }

            body.OptimizeMacros(); // Optimize again (paired with 'SimplifyMacros').

            return sites.Count;
        }

        private Candidate FindContextOverload(MethodReference callee)
        {
            // NOTE:
            // Finds an overload on UnityEngine.Debug whose parameter list equals
            // the callee's with a single UnityEngine.Object parameter inserted somewhere.
            // EX:
            // Log(object)                 → Log(object, Object)                 (ContextIndex = 1)
            // LogFormat(string, object[]) → LogFormat(Object, string, object[]) (ContextIndex = 0)
            // Assert(bool, string)        → Assert(bool, string, Object)        (ContextIndex = 2)

            if (_candidateByCalleeName.TryGetValue(callee.FullName, out var cached))
            {
                return cached;
            }

            var candidate = default(Candidate);

            foreach (var parameter in callee.Parameters) // If Already has a context parameter.
            {
                if (parameter.ParameterType.FullName == UnityObjectName)
                {
                    _candidateByCalleeName[callee.FullName] = candidate;

                    return candidate;
                }
            }

            var debugType = ResolveDebugType(callee);

            if (debugType != null)
            {
                var wantedCount = callee.Parameters.Count + 1;

                foreach (var method in debugType.Methods)
                {
                    if (!method.IsStatic
                    ||  !method.IsPublic
                    ||   method.Name             != callee.Name
                    ||   method.Parameters.Count != wantedCount)
                    {
                        continue;
                    }

                    var contextIndex = IndexOfInsertedContext(callee, method);
                    if (contextIndex < 0)
                    {
                        continue;
                    }

                    candidate.Target       = _module.ImportReference(method);
                    candidate.ContextIndex = contextIndex;

                    break;
                }
            }

            _candidateByCalleeName[callee.FullName] = candidate;

            return candidate;
        }

        private TypeDefinition ResolveDebugType(MethodReference callee)
        {
            if (_debugTypeResolved)
            {
                return _debugType;
            }

            _debugTypeResolved = true;

            try
            {
                _debugType = callee.DeclaringType.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                _debugType = null;
            }

            return _debugType;
        }

        private static int IndexOfInsertedContext(MethodReference callee, MethodDefinition candidate)
        {
            for (var contextIndex = 0; contextIndex < candidate.Parameters.Count; contextIndex++)
            {
                if (candidate.Parameters[contextIndex].ParameterType.FullName != UnityObjectName)
                {
                    continue;
                }

                var matches = true;

                for (int calleeIndex    = 0,
                         candidateIndex = 0;
                         candidateIndex < candidate.Parameters.Count;
                         candidateIndex++)
                {
                    if (candidateIndex == contextIndex)
                    {
                        continue;
                    }

                    if (callee   .Parameters[calleeIndex]   .ParameterType.FullName
                    !=  candidate.Parameters[candidateIndex].ParameterType.FullName)
                    {
                        matches = false;
                        break;
                    }

                    calleeIndex++;
                }

                if (matches)
                {
                    return contextIndex;
                }
            }

            return -1;
        }

        private void RewriteCall(ILProcessor il, MethodBody body, CallSite site)
        {
            // KEYWORDS:
            // - ldarg: An IL (Intermediate Language) instruction, short for "load argument".
            //          It pushes an argument of the current method onto the evaluation stack.
            //          ldarg.0 pushes argument 0 onto the stack. In an instance method, ldarg.0 is 'this'.
            //          Since only non-static methods of UnityEngine.Object-derived types are processed,
            //          'this' can be passed as 'context'.
            // - spill: temporarily saving values on the stack into local variables.
            //          The term comes from the compiler concept of "register spilling".

            // NOTE:
            // Stack before the call: [p0…p(n-1)] with p(n-1) on top.
            // (n = parameters.Count)
            // 
            // EX:
            // Debug.LogFormat(string format, object[] args)
            // - p(n-1) : args object[]
            // - p0     : format string
            // 
            // LogFormat(string format, object[] args) should be replaced
            // with LogFormat(Object 'context', string format, object[] args).
            // Here, 'ContextIndex' is 0, but pushing 'context' puts it on top of the stack,
            // which is the position of the last argument.
            // 
            // Since values can only be pushed onto the top of the stack,
            // spill the arguments from ContextIndex onward into local variables,
            // push 'context', and then restore the spilled arguments.
            // 
            // ※1
            // When ContextIndex is not 0, 'context' goes at position ContextIndex in the argument list
            // (counting from p0, not from the bottom of the whole stack).
            // ※2
            // When 'context' comes last, such as Log(object) → Log(object, context:Object),
            // ContextIndex is 1 and parameters.Count is also 1, so spillCount is 0.
            // Neither spilling nor restoring occurs; the call becomes just 'ldarg.0' + 'call Target'.

            // CAUTION:
            // Do not remove or replace the original call instruction.
            // Instead, reuse it in place as the first instruction of the new sequence,
            // and insert the remaining instructions after it.
            // This is because other parts of the method body may be referencing site.Call as an object.
            //
            // EX:
            // - Branch    : Debug.Log(flag ? "a" : "b") makes the call a branch target.
            // - Switch    : a 'switch' instruction holds its targets as an Instruction[] operand.
            // - Exception : ExceptionHandler.TryStart / TryEnd / HandlerStart / HandlerEnd / FilterStart.
            //               catch (Exception e) { Debug.LogException(e); } can make the call
            //               the first instruction of the handler in optimized builds.
            // - Debug info: sequence points (breakpoints / stepping) are attached to instructions.
            //
            // If the original instruction is removed and another one is inserted,
            // branch, switch and exception handler references would keep pointing to the removed instruction.
            // This results in invalid IL. In addition, the sequence point attached to it would be lost.

            var parameters = site.Callee.Parameters;
            var spillCount = parameters.Count - site.Candidate.ContextIndex;
            var locals     = new VariableDefinition[spillCount];

            for (var spillIndex = 0; spillIndex < spillCount; spillIndex++)
            {
                var parameterType = parameters[site.Candidate.ContextIndex + spillIndex].ParameterType;

                locals[spillIndex] = new VariableDefinition(_module.ImportReference(parameterType));

                body.Variables.Add(locals[spillIndex]);
            }

            var sequence = new List<Instruction>(spillCount * 2 + 2);

            for (var spillIndex = spillCount - 1; 0 <= spillIndex; spillIndex--)
            {
                sequence.Add(il.Create(OpCodes.Stloc, locals[spillIndex])); // pops top of stack first
            }

            sequence.Add(il.Create(OpCodes.Ldarg_0));

            for (var spillIndex = 0; spillIndex < spillCount; spillIndex++)
            {
                sequence.Add(il.Create(OpCodes.Ldloc, locals[spillIndex]));
            }

            sequence.Add(il.Create(OpCodes.Call, site.Candidate.Target));

            var first = sequence[0];

            site.Call.OpCode  = first.OpCode;
            site.Call.Operand = first.Operand;

            var cursor = site.Call;

            for (var instructionIndex = 1; instructionIndex < sequence.Count; instructionIndex++)
            {
                il.InsertAfter(cursor, sequence[instructionIndex]);
                cursor = sequence[instructionIndex];
            }
        }
    }
}}