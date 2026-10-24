using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace WpfAsyncScenarios;

internal static class ClassicCodegen
{
    internal static void Verify()
    {
        var methods = typeof(Runner).Assembly.GetTypes().SelectMany(type =>
            type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)).ToArray();
        if (methods.Any(method => ((int)method.GetMethodImplementationFlags() & 0x2000) != 0))
            throw new InvalidOperationException("The sample assembly contains a runtime-async method.");
        var asyncMethods = methods.Where(method => method.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
            .Select(method =>
            {
                Type stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
                MethodInfo moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                if (!typeof(IAsyncStateMachine).IsAssignableFrom(stateMachine) || moveNext == null ||
                    ((int)moveNext.GetMethodImplementationFlags() & 0x2000) != 0)
                    throw new InvalidOperationException($"Invalid classic state machine for {method.Name}.");
                byte[] il = moveNext.GetMethodBody()!.GetILAsByteArray()!;
                return new
                {
                    DeclaringType = method.DeclaringType!.FullName, Method = method.Name,
                    Token = method.MetadataToken, Flags = (int)method.GetMethodImplementationFlags(),
                    RuntimeAsync0x2000 = false, ClassicStateMachine = stateMachine.FullName,
                    IAsyncStateMachine = true, MoveNextToken = moveNext.MetadataToken,
                    MoveNextIlSha256 = Convert.ToHexString(SHA256.HashData(il))
                };
            }).ToArray();
        var scenarioMethods = methods.Where(method => method.DeclaringType == typeof(Runner) &&
            method.Name.EndsWith("Async", StringComparison.Ordinal)).ToArray();
        if (scenarioMethods.Length == 0 || scenarioMethods.Any(method => method.GetCustomAttribute<AsyncStateMachineAttribute>() == null))
            throw new InvalidOperationException("Every scenario async method must have a classic state machine.");
        Evidence.Phase("codegen.verified", new
        {
            ClassicMethods = asyncMethods, ScenarioMethodCount = scenarioMethods.Length,
            RuntimeMethodsInSampleAssembly = 0,
            Scope = "All sample-owned async bodies are classic; framework implementation families are not changed."
        });
    }
}
